using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Answering;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>
/// A question that needs a notice first and then its documents, with scripted model replies and the real tools.
/// The script shows that the agent lets a model work in steps and correct a rejected call; whether a real model
/// does so is measured by the evaluation.
/// </summary>
public sealed class AgentTwoStepTests(PostgresFixture database)
{
    private const string Number = "930111-2026";
    private const string Passage = "Wadium wynosi 12 000 zł.";

    private static readonly Notice[] Notices =
    [
        new(Number, "Dostawa ambulansów", "Pogotowie w Jasnym Brzegu", null, [], new DateOnly(2026, 11, 3), null, null),
        new("930222-2026", "Dostawa ambulansu transportowego", "Szpital w Jasnym Brzegu", null, [], new DateOnly(2026, 11, 2), null, null),
    ];

    private static readonly DocumentToolTests.Chunk[] Chunks =
    [
        new(Number, "swz-a", 4, Passage, DocumentToolTests.Vector(1, 0)),
        new("930222-2026", "swz-b", 6, "Wadium wynosi 99 000 zł.", DocumentToolTests.Vector(1, 0)),
    ];

    private static readonly (string Tool, object Arguments) SearchNotices = ("search_notices", new { query = "ambulansów" });

    private static readonly (string Tool, object Arguments) SearchDocuments = ("search_tender_documents", new { query = "wadium", publicationNumber = Number });

    /// <summary>A document search sent before the model knows the number: the tool rejects it.</summary>
    private static readonly (string Tool, object Arguments) TooEarly = ("search_tender_documents", new { query = "wadium", publicationNumber = "<numer ogłoszenia>" });

    [Fact]
    public async Task NoticeSearchThenDocumentSearch_EachGetsTheResultBeforeIt_AndTheAnswerCitesThatTender()
    {
        await using var agent = await StartAsync();
        agent.Chat.Calls(SearchNotices).Calls(SearchDocuments).Text().Answer("Wadium wynosi 12 000 zł.", 2);

        var response = await agent.AskAsync();

        Assert.Equal(["call-1"], ResultIds(agent, request: 1));
        Assert.Equal(["call-1", "call-2"], ResultIds(agent, request: 2));
        Assert.Contains(Number, agent.Chat.ToolResult("call-1"), StringComparison.Ordinal);
        Assert.DoesNotContain("930222-2026", agent.Chat.ToolResult("call-1"), StringComparison.Ordinal);
        Assert.Equal([("search_notices", false), ("search_tender_documents", false)], response.ToolCalls.Select(c => (c.Name, c.Failed)));
        Assert.Equal(new Citation("swz-a", Number, 4, Passage), Assert.Single(response.Citations));
        Assert.False(response.LimitReached);
        Assert.Equal(4, agent.Chat.Requests.Count);
    }

    [Fact]
    public async Task DocumentSearchSentWithTheNoticeSearch_IsRejected_AndTheCorrectedCallGivesTheSource()
    {
        await using var agent = await StartAsync();
        agent.Chat.Calls(SearchNotices, TooEarly).Calls(SearchDocuments).Text().Answer("Wadium wynosi 12 000 zł.", 2);

        var response = await agent.AskAsync();

        // The second request holds the notice and the tool's own message about the number.
        Assert.Contains(Number, agent.Chat.ToolResult("call-1"), StringComparison.Ordinal);
        Assert.Contains("publicationNumber must be 1 to 6 digits", agent.Chat.ToolResult("call-2"), StringComparison.Ordinal);
        Assert.Equal(
            [("search_notices", false), ("search_tender_documents", true), ("search_tender_documents", false)],
            response.ToolCalls.Select(c => (c.Name, c.Failed)));
        Assert.Equal("<numer ogłoszenia>", response.ToolCalls[1].Arguments!.Value.GetProperty("publicationNumber").GetString());
        Assert.Equal(new Citation("swz-a", Number, 4, Passage), Assert.Single(response.Citations));
        Assert.False(response.LimitReached);
        Assert.Equal(4, agent.Chat.Requests.Count);
    }

    [Fact]
    public async Task CorrectionInTheFourthReply_IsRun_AndTheRunReportsTheLimit()
    {
        await using var agent = await StartAsync();
        agent.Chat
            .Calls(SearchNotices)
            .Calls(("get_notice", new { publicationNumber = Number }))
            .Calls(TooEarly)
            .Calls(SearchDocuments)
            .Answer("Wadium wynosi 12 000 zł.", 2);

        var response = await agent.AskAsync();

        Assert.Equal([false, false, true, false], response.ToolCalls.Select(c => c.Failed));
        Assert.Equal(Passage, Assert.Single(response.Citations).Passage);
        Assert.True(response.LimitReached);
        Assert.Equal(5, agent.Chat.Requests.Count);
    }

    [Theory]
    [InlineData("ambulansów", false, 5)]
    [InlineData("nieistniejące", true, 4)]
    public async Task RejectionInTheFourthReply_EndsTheGathering_AndTheAnswerUsesWhatWasFoundBefore(string query, bool refused, int requests)
    {
        await using var agent = await StartAsync();
        var search = ("search_notices", (object)new { query });
        agent.Chat.Calls(search).Calls(search).Calls(search).Calls(TooEarly).Answer("Jest takie ogłoszenie.", 1);

        var response = await agent.AskAsync();

        Assert.Equal([false, false, false, true], response.ToolCalls.Select(c => c.Failed));
        Assert.True(response.LimitReached);
        Assert.Equal(refused, response.Refused);
        Assert.Empty(response.Citations);
        Assert.Equal(requests, agent.Chat.Requests.Count);
    }

    [Fact]
    public async Task SearchThatFindsNothing_IsNotARejectedCall()
    {
        await using var agent = await StartAsync();
        agent.Chat.Calls(("search_notices", new { query = "nieistniejące" })).Text();

        var response = await agent.AskAsync();

        Assert.False(Assert.Single(response.ToolCalls).Failed);
        Assert.True(response.Refused);
        Assert.False(response.LimitReached);
        Assert.Equal(2, agent.Chat.Requests.Count);
    }

    private async Task<AgentTestHost> StartAsync() => new(await AgentTestHost.SeedAsync(database, Notices, Chunks));

    /// <summary>The tool calls whose results a gathering request holds; requests are counted from zero.</summary>
    private static IEnumerable<string> ResultIds(AgentTestHost agent, int request) =>
        agent.Chat.Requests[request].Messages.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionResultContent>().Select(result => result.CallId);
}
