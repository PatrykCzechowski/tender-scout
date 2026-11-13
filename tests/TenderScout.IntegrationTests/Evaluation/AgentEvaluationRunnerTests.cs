using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenderScout.Domain.Notices;
using TenderScout.Evaluation;
using TenderScout.IntegrationTests.Agent;
using TenderScout.IntegrationTests.Tools;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>
/// The command for the agent cases, with scripted model replies, the real agent and tools, and a test database.
/// The clock is a fake one at another date than the reference date of the cases.
/// </summary>
public sealed class AgentEvaluationRunnerTests(PostgresFixture database) : IDisposable
{
    private const string GetPacs = """{"id": "x-1", "question": "QUESTION-MARKER Podaj dane ogłoszenia 100202-2026.", "expectedCalls": [{"tool": "get_notice", "arguments": {"publicationNumber": "100202-2026"}}], "allowedExtraCalls": []}""";
    private const string NoCall = """{"id": "x-2", "question": "QUESTION-MARKER Ile to dwa plus dwa?", "expectedCalls": [], "allowedExtraCalls": []}""";

    private static readonly string PublicCases = Path.Combine(AppContext.BaseDirectory, "Evaluation", "agent");
    private static readonly (string Tool, object Arguments) Get = ("get_notice", new { publicationNumber = "100202-2026" });

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("tender-scout-agent-eval-");
    private readonly ScriptedChatClient _chat = new();
    private readonly FakeEmbeddingGenerator _generator = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2031, 5, 5, 22, 30, 0, TimeSpan.Zero));
    private readonly StringWriter _output = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string OutputFolder => Path.Combine(_folder.FullName, "eval");

    [Fact]
    public async Task PublicCases_AreLoadedIntoAnEmptyDatabase_AndEachIsAskedThreeTimesInOrder()
    {
        var connection = await database.CreateIsolatedDatabaseAsync(targetMigration: "0");
        var set = AgentCaseSet.Load(PublicCases);
        for (var i = 0; i < set.Cases.Count * AgentEvaluationRunner.RunsPerCase; i++)
        {
            _chat.Text();
        }

        var exitCode = await RunAsync(connection, PublicCases);

        Assert.Equal(0, exitCode);
        Assert.Equal(set.Cases.SelectMany(c => Enumerable.Repeat(c.Question, 3)), _chat.Requests.Select(r => r.Messages[1].Text));
        Assert.All(_chat.Requests, request => Assert.Equal((SyntheticSetDatabase.AgentModel, 3), (request.Options!.ModelId, request.Options.Tools!.Count)));
        await using var dbContext = database.CreateDbContext(connection);
        Assert.Equal((10, 5, 30), (await dbContext.Notices.CountAsync(Ct), await dbContext.Documents.CountAsync(Ct), await dbContext.DocumentChunks.CountAsync(Ct)));

        var (file, results) = await ReadResultsAsync();
        Assert.Equal("agent-20310505-223000.json", Path.GetFileName(file));
        Assert.Equal((12, 3, true, 36), (results.Cases, results.RunsPerCase, results.Complete, results.CaseRuns.Count));
        Assert.Equal((4, 12, 120), (results.MaxRoundTrips, results.MaxToolCalls, results.GatheringSeconds));
        Assert.Equal((SyntheticSetDatabase.AgentModel, "bge-m3", new DateOnly(2026, 12, 7)), (results.AgentModel, results.EmbeddingModel, results.ReferenceDate));

        // A model that calls no tool passes only the two cases that expect none.
        Assert.Equal((new Share(0, 42), new Share(6, 36)), (results.Overall.ExpectedToolChosen, results.Overall.CasesPassed));
        var printed = _output.ToString();
        Assert.StartsWith($"Agent model {SyntheticSetDatabase.AgentModel}, embedding model bge-m3, database test_", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("| agent-12 | n/a (0/0) | n/a (0/0) | 0-0 | 0 | 0 | 3/3 |", printed, StringComparison.Ordinal);
        Assert.Contains("| Cases passed | 0.17 (6/36) | 0.17-0.17 |", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_IsToldTheReferenceDate_ItsTimeLimitStillRuns_AndARunChangesNoRow()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        _chat.Calls(Get).Waits().Answer("Jest takie ogłoszenie.", 1).Text().Text();

        var running = RunAsync(connection, Cases(GetPacs));
        await _chat.Waiting.Task.WaitAsync(Ct);
        await using var dbContext = database.CreateDbContext(connection);
        var loaded = await ToolContractTests.SnapshotAsync(dbContext);
        _clock.Advance(TimeSpan.FromSeconds(121));
        var exitCode = await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(0, exitCode);
        Assert.Contains("Dzisiaj jest 2026-12-07 (poniedziałek).", _chat.Requests[0].Messages[0].Text, StringComparison.Ordinal);
        var (_, results) = await ReadResultsAsync();
        Assert.Equal(new RunScore(1, 1, 1, 1, true, 0, 0, 0, false), results.CaseRuns[0].Score);
        Assert.True(results.CaseRuns[0].Trajectory!.LimitReached);
        Assert.Equal((1, 0), (results.Overall.AnyGatheringLimit, results.Overall.RunsAtCallCap));
        Assert.Equal(loaded, await ToolContractTests.SnapshotAsync(dbContext));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseThatHoldsANoticeOrADocument_IsRefused_AndNotTouched(bool holdsNotice)
    {
        Notice[] notices = holdsNotice ? [new("999001-2026", "Istniejące ogłoszenie", null, null, [], new DateOnly(2026, 11, 2), null, null)] : [];
        DocumentToolTests.Chunk[] chunks = holdsNotice ? [] : [new("999002-2026", "swz-existing", 1, "Istniejący dokument.", DocumentToolTests.Vector(1, 0))];
        var connection = await AgentTestHost.SeedAsync(database, notices, chunks);
        await using var dbContext = database.CreateDbContext(connection);
        var before = await ToolContractTests.SnapshotAsync(dbContext);

        var exitCode = await RunAsync(connection, PublicCases);

        Assert.Equal(1, exitCode);
        Assert.Contains("already holds notices or documents", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, await ToolContractTests.SnapshotAsync(dbContext));
        Assert.Empty(_chat.Requests);
        Assert.False(Directory.Exists(OutputFolder));
    }

    [Fact]
    public async Task ResultsFile_HoldsTheToolCallsAndScoresOnly_AndGivesTheSameScoresAgain()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var folder = Cases(GetPacs, NoCall);
        _chat
            .Calls(Get).Text().Answer("ANSWER-MARKER", 1)
            .Calls(("get_notice", new { publicationNumber = "abc" })).Text()
            .Calls(("no_such_tool", new { text = "x" })).Text()
            .CallWithUnreadableArguments("get_notice").Text()
            .Text()
            .Calls(("search_tender_documents", new { query = "QUERY-MARKER wadium" })).Text().Answer("ANSWER-MARKER", 1);

        var exitCode = await RunAsync(connection, folder);

        Assert.Equal(0, exitCode);
        var (file, results) = await ReadResultsAsync();
        Assert.Equal(
            [
                new RunScore(1, 1, 1, 1, false, 0, 0, 0, true),
                new RunScore(1, 1, 0, 1, false, 0, 0, 1, false),
                new RunScore(1, 0, 0, 1, false, 1, 1, 1, false),
                new RunScore(0, 0, 0, 1, false, 1, 0, 1, false),
                new RunScore(0, 0, 0, 0, false, 0, 0, 0, true),
                new RunScore(0, 0, 0, 1, false, 1, 0, 0, false),
            ],
            results.CaseRuns.Select(run => run.Score));
        Assert.Equal(["x-1 1", "x-1 2", "x-1 3", "x-2 1", "x-2 2", "x-2 3"], results.CaseRuns.Select(run => $"{run.CaseId} {run.Run}"));
        Assert.Equal([false, true, true, true, true, false], results.CaseRuns.Select(run => run.Trajectory!.Refused));
        Assert.Null(results.CaseRuns[3].Trajectory!.Calls[0].Arguments);

        // Arguments are kept as the model sent them; nothing a tool returned, no question and no answer is.
        var json = await File.ReadAllTextAsync(file, Ct);
        var passages = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Evaluation", SyntheticSet.PassagesFile))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("text").GetString()!);
        Assert.Contains("QUERY-MARKER wadium", json, StringComparison.Ordinal);
        Assert.All(["QUESTION-MARKER", "ANSWER-MARKER", "PACS", "durationMs", "answer", "citations"], text => Assert.DoesNotContain(text, json, StringComparison.Ordinal));
        Assert.All(passages, passage => Assert.DoesNotContain(JsonEncodedText.Encode(passage).ToString(), json, StringComparison.Ordinal));

        // The file and the cases are enough to score again, and to print the same tables.
        var cases = AgentCaseSet.Load(folder).Cases.ToDictionary(c => c.Id);
        var tables = new StringWriter();
        await Summary.WriteAsync(tables, results);
        Assert.All(results.CaseRuns, run => Assert.Equal(run.Score, AgentScoring.Score(cases[run.CaseId], run.Trajectory!)));
        Assert.Equal(results.Overall, AgentScoring.Summarize(results.CaseRuns, results.MaxToolCalls));
        Assert.Contains(tables.ToString(), _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("embedding")]
    public async Task ModelThatCannotBeReached_StopsTheRun_AndTheFileSaysItIsIncomplete(string model)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        _chat.Text();
        if (model == "chat")
        {
            _chat.Fails(new HttpRequestException("Connection refused (http://192.0.2.99:11434/api/chat)"));
        }
        else
        {
            _generator.FailOnCall = 1;
            _chat.Calls(("search_tender_documents", new { query = "wadium" }));
        }

        var exitCode = await RunAsync(connection, Cases(GetPacs, NoCall));

        Assert.Equal(1, exitCode);
        Assert.Equal(2, _chat.Requests.Count);
        var (file, results) = await ReadResultsAsync();
        Assert.False(results.Complete);
        Assert.Equal("x-1 1", Assert.Single(results.CaseRuns.Select(run => $"{run.CaseId} {run.Run}")));
        Assert.Contains($"The run is incomplete; what was recorded is in {file}.", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("| Case |", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaseRunThatThrows_IsRecordedAsErrored_AndTheOtherRunsAreMade()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        _chat
            .Calls(Get).Text().Answer("Jest takie ogłoszenie.", 1)
            .Fails(new InvalidOperationException("internal-detail-marker"))
            .Text();

        var exitCode = await RunAsync(connection, Cases(GetPacs));

        Assert.Equal(1, exitCode);
        var (file, results) = await ReadResultsAsync();
        Assert.True(results.Complete);
        Assert.Equal(new CaseRun("x-1", 2, null, null, "InvalidOperationException"), results.CaseRuns[1]);
        Assert.Equal(
            new AgentSummary(new(1, 2), new(1, 2), new(1, 3), 0.5, 0, 1, 0, 0, 0, 0, 0, 1),
            results.Overall);
        Assert.Contains("| Errored case runs | 1 | |", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("internal-detail-marker", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate id", "x-1")]
    [InlineData("no agent model", "Agent:Model")]
    public async Task BrokenCaseFileOrMissingModel_StopsBeforeAnythingIsLoadedOrAsked(string problem, string named)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var folder = problem == "duplicate id" ? Cases(GetPacs, GetPacs) : Cases(GetPacs);

        var exitCode = await RunAsync(connection, folder, agentModel: problem == "no agent model" ? " " : SyntheticSetDatabase.AgentModel);

        Assert.Equal(1, exitCode);
        Assert.Contains(named, _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_chat.Requests);
        await using var dbContext = database.CreateDbContext(connection);
        Assert.False(await dbContext.Notices.AnyAsync(Ct));
        Assert.False(Directory.Exists(OutputFolder));
    }

    public void Dispose()
    {
        _folder.Delete(recursive: true);
        _output.Dispose();
    }

    private async Task<int> RunAsync(string connection, string caseFolder, string agentModel = SyntheticSetDatabase.AgentModel)
    {
        await using var services = SyntheticSetDatabase.CreateServices(connection, _generator, _chat, _clock, agentModel);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AgentEvaluationRunner>().RunAsync(
            caseFolder,
            Path.Combine(AppContext.BaseDirectory, "Evaluation", SyntheticSet.PassagesFile),
            OutputFolder,
            _output,
            Ct);
    }

    /// <summary>A case folder with the given cases and the notices and reference date of the public set.</summary>
    private string Cases(params string[] cases)
    {
        var folder = _folder.CreateSubdirectory("cases").FullName;
        File.WriteAllLines(Path.Combine(folder, AgentCaseSet.CasesFile), cases);
        File.Copy(Path.Combine(PublicCases, AgentCaseSet.NoticesFile), Path.Combine(folder, AgentCaseSet.NoticesFile));
        File.Copy(Path.Combine(PublicCases, AgentCaseSet.ReferenceDateFile), Path.Combine(folder, AgentCaseSet.ReferenceDateFile));
        return folder;
    }

    private async Task<(string File, AgentEvaluationResults Results)> ReadResultsAsync()
    {
        var file = Assert.Single(Directory.GetFiles(OutputFolder));
        return (file, JsonSerializer.Deserialize<AgentEvaluationResults>(await File.ReadAllTextAsync(file, Ct), JsonSerializerOptions.Web)!);
    }
}
