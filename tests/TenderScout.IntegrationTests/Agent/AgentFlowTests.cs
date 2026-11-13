using System.Text.Json;
using Microsoft.Extensions.AI;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Answering;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>A whole run: several steps, what is logged about them, and that nothing is written.</summary>
public sealed class AgentFlowTests(PostgresFixture database)
{
    private static readonly Notice Servers = new(
        "111111-2026", "Dostawa serwerow TITLE-MARKER", "Gmina Przykladowo", "48820000", ["48820000"], new DateOnly(2026, 11, 4), new DateOnly(2026, 12, 1), null);

    private static readonly Notice Portal = new("333333-2026", "Portal pacjenta", null, "72000000", [], new DateOnly(2026, 11, 2), null, null);

    private static readonly DocumentToolTests.Chunk Deposit = new("111111-2026", "swz-a", 3, "Wadium wynosi 10 000 zł. PASSAGE-MARKER", DocumentToolTests.Vector(1, 0));

    private static readonly (string Tool, object Arguments) SearchNotices = ("search_notices", new { query = "serwer" });

    private static readonly (string Tool, object Arguments) GetServers = ("get_notice", new { publicationNumber = "111111-2026" });

    private static readonly (string Tool, object Arguments) SearchDocuments = ("search_tender_documents", new { query = "wadium", publicationNumber = "111111-2026" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchThenReadAndDocuments_AnswersFromANoticeAndAPassage_InThreeRoundTrips()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers, Portal], Deposit));
        agent.Chat.Calls(SearchNotices).Calls(GetServers, SearchDocuments).Text().Answer("Wadium wynosi 10 000 zł, oferty do 1 grudnia.", 1, 2);

        var response = await agent.AskAsync();

        Assert.False(response.Refused);
        Assert.False(response.LimitReached);
        Assert.Equal("Wadium wynosi 10 000 zł, oferty do 1 grudnia.", response.Answer);
        Assert.Equal("111111-2026", Assert.Single(response.Notices).PublicationNumber);
        Assert.Equal(new Citation("swz-a", "111111-2026", 3, Deposit.Text), Assert.Single(response.Citations));
        Assert.Equal(
            ["search_notices {\"query\":\"serwer\"}", "get_notice {\"publicationNumber\":\"111111-2026\"}", "search_tender_documents {\"query\":\"wadium\",\"publicationNumber\":\"111111-2026\"}"],
            response.ToolCalls.Select(c => $"{c.Name} {c.Arguments?.GetRawText()}"));
        Assert.All(response.ToolCalls, call => Assert.True(call is { Failed: false, DurationMs: >= 0 }));

        // Each round is given the results of the round before; the answering call has the sources and no tools.
        Assert.Equal(4, agent.Chat.Requests.Count);
        Assert.Equal([[], ["call-1"], ["call-1", "call-2", "call-3"]], agent.Chat.Requests.Take(3).Select(ResultIds));
        Assert.Contains("TITLE-MARKER", agent.Chat.ToolResult("call-1"), StringComparison.Ordinal);
        Assert.Null(agent.Chat.Requests[3].Options!.Tools);
        Assert.Empty(ResultIds(agent.Chat.Requests[3]));
    }

    [Fact]
    public async Task EveryToolCallAndTheRun_AreLoggedOnce_AsTheResponseListsThem_WithoutWhatTheToolsReturned()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers], Deposit));
        agent.Chat
            .Calls(SearchNotices, ("get_notice", new { publicationNumber = "abc" }), ("no_such_tool", new { text = "x" }), SearchDocuments)
            .CallWithUnreadableArguments("get_notice")
            .Text()
            .Answer("Wadium wynosi 10 000 zł. ANSWER-MARKER", 1, 2);

        var response = await agent.AskAsync();

        // In the order the model asked, also the call to the tool that does not exist, which the framework answers.
        Assert.Equal(
            [("search_notices", false), ("get_notice", true), ("no_such_tool", true), ("search_tender_documents", false), ("get_notice", true)],
            response.ToolCalls.Select(c => (c.Name, c.Failed)));
        Assert.Null(response.ToolCalls[4].Arguments);
        var logged = agent.AgentLog.Where(entry => entry.Contains("Agent tool call", StringComparison.Ordinal)).ToList();
        Assert.Equal(response.ToolCalls.Count, logged.Count);
        Assert.All(response.ToolCalls.Zip(logged), pair => Assert.StartsWith(
            $"Information: Agent tool call {pair.First.Name} with {pair.First.Arguments?.GetRawText() ?? "null"} took {pair.First.DurationMs} ms; failed: {pair.First.Failed} ",
            pair.Second,
            StringComparison.Ordinal));
        Assert.StartsWith(
            "Information: Agent run answered: 3 round trips, 5 tool calls, limit reached: False, sources gathered: 2, used: 2, ",
            agent.AgentLog.Single(entry => entry.Contains("Agent run", StringComparison.Ordinal)),
            StringComparison.Ordinal);
        Assert.Equal(6, agent.AgentLog.Count());

        Assert.Contains("ANSWER-MARKER", response.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain(agent.AllLog, entry =>
            entry.Contains("TITLE-MARKER", StringComparison.Ordinal)
            || entry.Contains("PASSAGE-MARKER", StringComparison.Ordinal)
            || entry.Contains("ANSWER-MARKER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_ChangeNothingInTheDatabase()
    {
        var connection = await AgentTestHost.SeedAsync(database, [Servers, Portal], Deposit);
        await using var dbContext = database.CreateDbContext(connection);
        var before = await ToolContractTests.SnapshotAsync(dbContext);
        await using var agent = new AgentTestHost(connection);
        agent.Chat
            .Calls(SearchNotices, SearchDocuments).Text().Answer("Wadium wynosi 10 000 zł.", 2)
            .Text()
            .Calls(("get_notice", new { publicationNumber = "abc" }), ("no_such_tool", new { })).Text()
            .Calls(GetServers).Calls(GetServers).Calls(GetServers).Calls(GetServers).Answer("Jest jedno ogłoszenie.", 1);

        var answered = await agent.AskAsync();
        var withoutTools = await agent.AskAsync();
        var invalidCalls = await agent.AskAsync();
        var limited = await agent.AskAsync();

        Assert.Equal(
            [(false, false), (true, false), (true, false), (false, true)],
            new[] { answered, withoutTools, invalidCalls, limited }.Select(r => (r.Refused, r.LimitReached)));
        Assert.Equal(before, await ToolContractTests.SnapshotAsync(dbContext));
    }

    private static IEnumerable<string> ResultIds((List<ChatMessage> Messages, ChatOptions? Options) request) =>
        request.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(result => result.CallId);
}
