using System.Text.Json;
using TenderScout.Evaluation;

namespace TenderScout.UnitTests.Evaluation;

/// <summary>
/// The scoring rules on hand-written trajectories, in the form the results file keeps them, with hand-counted
/// scores. No agent, model or database. A <see cref="RunScore"/> reads: expected, chosen, matched, steps, limit
/// hit, unexpected, unknown tools, failed calls, passed.
/// </summary>
public sealed class AgentScoringTests
{
    private const string ExpectGet = """[{"tool":"get_notice","arguments":{"publicationNumber":"100202-2026"}}]""";

    private const string ExpectNoticesThenDocuments = """
        [{"tool":"search_notices","arguments":{"query":{"words":["serwer"]}}},
         {"tool":"search_tender_documents","arguments":{"publicationNumber":"100202-2026","query":{"words":["wadium"]}}}]
        """;

    private static readonly string GoodGet = Get("100202-2026");

    private static readonly string GoodNotices = Call("search_notices", """{"query":"modernizacja serwerowni"}""");

    private static readonly string GoodDocuments = Call("search_tender_documents", """{"query":"wadium","publicationNumber":"100202-2026"}""");

    [Fact]
    public void ExactTrajectory_Passes_WhateverTheAnswerWas()
    {
        var answered = Score(Case(ExpectGet), Recorded(GoodGet));
        var refused = Score(Case(ExpectGet), Recorded(GoodGet) with { Refused = true });

        Assert.Equal(new RunScore(1, 1, 1, 1, false, 0, 0, 0, true), answered);
        Assert.Equal(answered, refused);
    }

    [Fact]
    public void RightToolWithAnotherOrAMissingArgument_IsChosenButNotMatched()
    {
        var chosenOnly = new RunScore(1, 1, 0, 1, false, 0, 0, 0, false);

        Assert.Equal(chosenOnly, Score(Case(ExpectGet), Recorded(Get("100303-2026"))));
        Assert.Equal(chosenOnly, Score(Case(ExpectGet), Recorded(Call("get_notice", "{}"))));
    }

    [Fact]
    public void ArgumentTheCaseDoesNotMention_IsNotChecked()
    {
        var call = Call("get_notice", """{"publicationNumber":"100202-2026","limit":3}""");

        Assert.True(Score(Case(ExpectGet), Recorded(call)).Passed);
    }

    [Fact]
    public void AnotherTool_IsAnExtraCall_UnexpectedUnlessTheCaseAllowsIt()
    {
        Assert.Equal(new RunScore(1, 0, 0, 1, false, 1, 0, 0, false), Score(Case(ExpectGet), Recorded(GoodNotices)));
        Assert.Equal(new RunScore(1, 0, 0, 1, false, 0, 0, 0, false), Score(Case(ExpectGet, "search_notices"), Recorded(GoodNotices)));
    }

    [Theory]
    [InlineData("\"100202-2026\"", "\" 100202-2026 \"", true)]
    [InlineData("\"SERWER\"", "\" serwer \"", true)]
    [InlineData("5", "\"5\"", true)]
    [InlineData("\"5\"", "5", true)]
    [InlineData("\"a b\"", "\"a  b\"", false)]
    [InlineData("\"sprzęt\"", "\"sprzet\"", false)]
    [InlineData("\"2026-11-16\"", "\"16.11.2026\"", false)]
    [InlineData("\"72\"", "true", false)]
    [InlineData("[\"72\", \"72000000\"]", "\"72\"", true)]
    [InlineData("[\"72\", \"72000000\"]", "\" 72000000 \"", true)]
    [InlineData("[\"72\", \"72000000\"]", "72000000", true)]
    [InlineData("[\"72\", \"72000000\"]", "\"73000000\"", false)]
    [InlineData("{\"words\": [\"wadium\", \"termin\"]}", "\"TERMIN wniesienia wadium\"", true)]
    [InlineData("{\"words\": [\"wadium\", \"termin\"]}", "\"terminowe wadium\"", true)]
    [InlineData("{\"words\": [\"wadium\", \"termin\"]}", "\"tylko wadium\"", false)]
    [InlineData("{\"words\": [\"5\"]}", "5", false)]
    public void Argument_IsAValue_OneOfAList_OrTextWithEveryWord(string expected, string actual, bool matches)
    {
        var @case = Case("""[{"tool":"search_notices","arguments":{"query":""" + expected + "}}]");

        var score = Score(@case, Recorded(Call("search_notices", """{"query":""" + actual + "}")));

        Assert.Equal((1, matches ? 1 : 0), (score.Chosen, score.Matched));
    }

    [Fact]
    public void ExpectedCalls_AreLookedForInOrder()
    {
        var reversed = Score(Case(ExpectNoticesThenDocuments), Recorded(GoodDocuments, GoodNotices));

        Assert.Equal(new RunScore(2, 1, 1, 2, false, 1, 0, 0, false), reversed);
    }

    [Fact]
    public void CorrectedRetry_MatchesTheFirstCorrectCall_AndTheOthersAreExtraCalls()
    {
        var rejectedThenGood = Recorded(Get("abc", failed: true), GoodGet);

        Assert.Equal(new RunScore(1, 1, 1, 2, false, 1, 0, 1, false), Score(Case(ExpectGet), rejectedThenGood));
        Assert.Equal(new RunScore(1, 1, 1, 2, false, 0, 0, 1, true), Score(Case(ExpectGet, "get_notice"), rejectedThenGood));
        Assert.Equal(new RunScore(1, 1, 1, 2, false, 1, 0, 0, false), Score(Case(ExpectGet), Recorded(Get("100303-2026"), GoodGet)));
        Assert.Equal(new RunScore(1, 1, 1, 2, false, 1, 0, 0, false), Score(Case(ExpectGet), Recorded(GoodGet, GoodGet)));
    }

    [Fact]
    public void FailedOrUnreadableCall_NeverMatches_AndAToolThatDoesNotExistIsAlwaysUnexpected()
    {
        var unreadable = """{"name":"get_notice","arguments":null,"failed":false}""";
        var unknown = Call("invented_lookup", """{"publicationNumber":"100202-2026"}""");

        Assert.Equal(new RunScore(1, 1, 0, 1, false, 0, 0, 1, false), Score(Case(ExpectGet), Recorded(Get("100202-2026", failed: true))));
        Assert.Equal(new RunScore(1, 1, 0, 1, false, 0, 0, 0, false), Score(Case(ExpectGet), Recorded(unreadable)));
        Assert.Equal(new RunScore(1, 0, 0, 1, false, 1, 1, 0, false), Score(Case(ExpectGet), Recorded(unknown)));
        Assert.Equal(new RunScore(1, 0, 0, 1, false, 1, 1, 1, false), Score(Case(ExpectGet), Recorded(unknown.Replace("false", "true", StringComparison.Ordinal))));
    }

    [Fact]
    public void LimitHit_FollowsTheFlag_AndFailsTheRun()
    {
        var limited = Score(Case(ExpectGet), Recorded(GoodGet) with { LimitReached = true });
        var limitedWithoutCalls = Score(Case("[]"), Recorded() with { LimitReached = true });

        Assert.Equal(new RunScore(1, 1, 1, 1, true, 0, 0, 0, false), limited);
        Assert.Equal(new RunScore(0, 0, 0, 0, true, 0, 0, 0, false), limitedWithoutCalls);
    }

    [Fact]
    public void RunsAtTheCallCap_AreLimitHitsWithExactlyTwelveCalls()
    {
        var anyNumberOfGets = Case("[]", "get_notice");
        var twelve = Recorded([.. Enumerable.Repeat(GoodGet, 12)]);
        CaseRun[] runs =
        [
            Run("a", 1, anyNumberOfGets, twelve),
            Run("a", 2, anyNumberOfGets, twelve with { LimitReached = true }),
            Run("a", 3, anyNumberOfGets, Recorded(GoodGet, GoodGet) with { LimitReached = true }),
        ];

        var summary = AgentScoring.Summarize(runs, callCap: 12);

        Assert.Equal((2, 1, 12), (summary.AnyGatheringLimit, summary.RunsAtCallCap, summary.MostSteps));
        Assert.Equal(new Share(1, 3), summary.CasesPassed);
    }

    [Fact]
    public void NoCallAtAll_FailsACaseWithExpectedCalls_AndPassesACaseWithout()
    {
        Assert.Equal(new RunScore(1, 0, 0, 0, false, 0, 0, 0, false), Score(Case(ExpectGet), Recorded()));
        Assert.Equal(new RunScore(0, 0, 0, 0, false, 0, 0, 0, true), Score(Case("[]"), Recorded()));
        Assert.Equal(new RunScore(0, 0, 0, 1, false, 1, 0, 0, false), Score(Case("[]"), Recorded(GoodGet)));
        Assert.Equal(new RunScore(0, 0, 0, 1, false, 0, 0, 0, true), Score(Case("[]", "get_notice"), Recorded(GoodGet)));
    }

    [Fact]
    public void AllowedExtraCalls_AreSteps_ButNotExpectedCalls()
    {
        const string expectNotices = """[{"tool":"search_notices","arguments":{"query":{"words":["serwer"]}}}]""";

        Assert.Equal(new RunScore(1, 1, 1, 3, false, 0, 0, 0, true), Score(Case(expectNotices, "get_notice"), Recorded(GoodNotices, GoodGet, GoodGet)));
        Assert.Equal(new RunScore(1, 1, 1, 3, false, 0, 0, 1, true), Score(Case(expectNotices, "get_notice"), Recorded(GoodNotices, Get("abc", failed: true), GoodGet)));
        Assert.Equal(new RunScore(1, 1, 1, 3, false, 2, 0, 0, false), Score(Case(expectNotices), Recorded(GoodNotices, GoodGet, GoodGet)));
    }

    [Fact]
    public void Call_IsTakenOnce_AndAMissingCallDoesNotMoveTheSearchOn()
    {
        const string getTwice = """
            [{"tool":"get_notice","arguments":{"publicationNumber":"100202-2026"}},
             {"tool":"get_notice","arguments":{"publicationNumber":"100202-2026"}}]
            """;
        const string noticesThenGet = """
            [{"tool":"search_notices","arguments":{}}, {"tool":"get_notice","arguments":{"publicationNumber":"100202-2026"}}]
            """;
        const string getAThenGetB = """
            [{"tool":"get_notice","arguments":{"publicationNumber":"100202-2026"}},
             {"tool":"get_notice","arguments":{"publicationNumber":"100303-2026"}}]
            """;

        Assert.Equal(new RunScore(2, 1, 1, 1, false, 0, 0, 0, false), Score(Case(getTwice), Recorded(GoodGet)));
        Assert.Equal(new RunScore(2, 1, 1, 1, false, 0, 0, 0, false), Score(Case(noticesThenGet), Recorded(GoodGet)));
        Assert.Equal(new RunScore(2, 1, 1, 2, false, 1, 0, 0, false), Score(Case(getAThenGetB), Recorded(Get("100303-2026"), GoodGet)));

        // No call matches the arguments: the first call to the tool is taken, the second is an extra call.
        Assert.Equal(new RunScore(1, 1, 0, 2, false, 1, 0, 0, false), Score(Case(ExpectGet), Recorded(Get("100303-2026"), Get("100404-2026"))));
    }

    /// <summary>
    /// Three cases, two runs each. A expects one get and allows more; B expects a notice search and then a document
    /// search; C expects no call.
    /// </summary>
    [Fact]
    public void Summary_CountsCallsOverAllRuns_PerCase_Overall_AndPerRun()
    {
        var a = Case(ExpectGet, "get_notice");
        var b = Case(ExpectNoticesThenDocuments);
        var c = Case("[]");
        var wrongNumber = Call("search_tender_documents", """{"query":"wadium","publicationNumber":"100303-2026"}""");
        var failedUnknown = Call("invented_lookup", "{}", failed: true);
        CaseRun[] runs =
        [
            Run("A", 1, a, Recorded(GoodGet)),
            Run("A", 2, a, Recorded(Get("abc", failed: true), GoodGet)),
            Run("B", 1, b, Recorded(GoodNotices, wrongNumber)),
            Run("B", 2, b, Recorded(GoodDocuments, GoodNotices, failedUnknown) with { LimitReached = true }),
            Run("C", 1, c, Recorded()),
            Run("C", 2, c, Recorded(GoodGet)),
        ];

        Assert.Equal(
            [
                new RunScore(1, 1, 1, 1, false, 0, 0, 0, true),
                new RunScore(1, 1, 1, 2, false, 0, 0, 1, true),
                new RunScore(2, 2, 1, 2, false, 0, 0, 0, false),
                new RunScore(2, 1, 1, 3, true, 2, 1, 1, false),
                new RunScore(0, 0, 0, 0, false, 0, 0, 0, true),
                new RunScore(0, 0, 0, 1, false, 1, 0, 0, false),
            ],
            runs.Select(run => run.Score));

        Assert.Equal(
            [
                new AgentSummary(new(2, 2), new(2, 2), new(2, 2), 1.5, 1, 2, 0, 0, 0, 0, 1, 0),
                new AgentSummary(new(3, 4), new(2, 4), new(0, 2), 2.5, 2, 3, 1, 0, 2, 1, 1, 0),
                new AgentSummary(new(0, 0), new(0, 0), new(1, 2), 0.5, 0, 1, 0, 0, 1, 0, 0, 0),
            ],
            runs.GroupBy(run => run.CaseId).Select(group => AgentScoring.Summarize([.. group], 12)));

        Assert.Equal(
            new AgentSummary(new(5, 6), new(4, 6), new(3, 6), 1.5, 0, 3, 1, 0, 3, 1, 2, 0),
            AgentScoring.Summarize(runs, 12));

        Assert.Equal(
            [
                new AgentSummary(new(3, 3), new(2, 3), new(2, 3), 1, 0, 2, 0, 0, 0, 0, 0, 0),
                new AgentSummary(new(2, 3), new(2, 3), new(1, 3), 2, 1, 3, 1, 0, 3, 1, 2, 0),
            ],
            runs.GroupBy(run => run.Run).Select(group => AgentScoring.Summarize([.. group], 12)));
    }

    [Fact]
    public async Task ShareOfNothing_IsNotANumber_InTheFileAndInTheTables()
    {
        var c = Case("[]");
        CaseRun[] runs = [Run("C", 1, c, Recorded()), Run("C", 2, c, Recorded(GoodGet))];
        var summary = AgentScoring.Summarize(runs, 12);
        var results = new AgentEvaluationResults(
            DateTimeOffset.UnixEpoch, "0123456789", "agent-model", "bge-m3", new DateOnly(2026, 12, 7), 4, 12, 120, 1, 2, true, summary,
            [.. runs.Select(run => AgentScoring.Summarize([run], 12))],
            runs);
        var tables = new StringWriter();

        await Summary.WriteAsync(tables, results);

        Assert.Null(summary.ExpectedToolChosen.Value);
        Assert.Equal("""{"count":0,"of":0,"value":null}""", JsonSerializer.Serialize(summary.ArgumentsMatched, JsonSerializerOptions.Web));
        Assert.Contains("| C | n/a (0/0) | n/a (0/0) | 0-1 | 0 | 1 | 1/2 |", tables.ToString(), StringComparison.Ordinal);
        Assert.Contains("| Expected tool chosen | n/a (0/0) | n/a |", tables.ToString(), StringComparison.Ordinal);
        Assert.Contains("| Cases passed | 0.50 (1/2) | 0.00-1.00 |", tables.ToString(), StringComparison.Ordinal);
        Assert.Contains("| Steps per case run: mean, least, most | 0.50, 0, 1 | |", tables.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ErroredRun_CountsAsNotPassed_AndAddsNothingElse()
    {
        var scored = Run("A", 1, Case(ExpectGet), Recorded(GoodGet));
        var errored = new CaseRun("A", 2, null, null, "InvalidOperationException");

        Assert.Equal(
            new AgentSummary(new(1, 1), new(1, 1), new(1, 2), 1, 1, 1, 0, 0, 0, 0, 0, 1),
            AgentScoring.Summarize([scored, errored], 12));
        Assert.Equal(
            new AgentSummary(new(0, 0), new(0, 0), new(0, 1), null, null, null, 0, 0, 0, 0, 0, 1),
            AgentScoring.Summarize([errored], 12));
    }

    private static RunScore Score(AgentCase @case, Trajectory trajectory) => AgentScoring.Score(@case, trajectory);

    private static CaseRun Run(string caseId, int run, AgentCase @case, Trajectory trajectory) =>
        new(caseId, run, trajectory, AgentScoring.Score(@case, trajectory), null);

    private static AgentCase Case(string expectedCalls, params string[] allowedExtraCalls) =>
        new("case", "Pytanie?", JsonSerializer.Deserialize<List<ExpectedCall>>(expectedCalls, JsonSerializerOptions.Web)!, allowedExtraCalls);

    /// <summary>A trajectory read from the JSON the results file holds.</summary>
    private static Trajectory Recorded(params string[] calls) =>
        JsonSerializer.Deserialize<Trajectory>($$"""{"calls":[{{string.Join(',', calls)}}],"limitReached":false,"refused":false}""", JsonSerializerOptions.Web)!;

    private static string Get(string publicationNumber, bool failed = false) =>
        Call("get_notice", $$"""{"publicationNumber":"{{publicationNumber}}"}""", failed);

    private static string Call(string name, string arguments, bool failed = false) =>
        $$"""{"name":"{{name}}","arguments":{{arguments}},"failed":{{(failed ? "true" : "false")}} }""";
}
