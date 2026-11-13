using Microsoft.Extensions.AI;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Answering;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>How gathering ends: by itself, or at the round, tool-call or time limit.</summary>
public sealed class AgentLimitsTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModelThatStopsByItself_TakesThreeRoundTripsAndOneAnswer_WithoutReachingALimit()
    {
        await using var agent = new AgentTestHost(await SeedNoticesAsync(2));
        agent.Chat.Calls(Get(1)).Calls(Get(2)).Text().Answer("Są dwa ogłoszenia.", 1, 2);

        var response = await agent.AskAsync();

        Assert.Equal(4, agent.Chat.Requests.Count);
        Assert.False(response.LimitReached);
        Assert.Equal(2, response.Notices.Count);
    }

    [Fact]
    public async Task ToolCallsOfTheFourthReply_AreStillRun_ThenTheAnswerComesWithLimitReached()
    {
        await using var agent = new AgentTestHost(await SeedNoticesAsync(4));
        agent.Chat.Calls(Get(1)).Calls(Get(2)).Calls(Get(3)).Calls(Get(4)).Answer("Ostatnie znalezione ogłoszenie.", 4);

        var response = await agent.AskAsync();

        Assert.Equal(5, agent.Chat.Requests.Count);
        Assert.True(response.LimitReached);
        Assert.Equal(4, response.ToolCalls.Count);
        Assert.Equal("100004-2026", Assert.Single(response.Notices).PublicationNumber);
        Assert.Null(agent.Chat.Requests[4].Options!.Tools);
    }

    [Fact]
    public async Task FourthReplyWithoutAToolCall_EndsGatheringNormally()
    {
        await using var agent = new AgentTestHost(await SeedNoticesAsync(3));
        agent.Chat.Calls(Get(1)).Calls(Get(2)).Calls(Get(3)).Text().Answer("Są trzy ogłoszenia.", 1);

        Assert.False((await agent.AskAsync()).LimitReached);
        Assert.Equal(5, agent.Chat.Requests.Count);
    }

    [Fact]
    public async Task ModelReplyThatHasNotComeAtTheTimeLimit_IsAbandoned_AndTheAnswerUsesWhatWasFound()
    {
        await using var agent = new AgentTestHost(await SeedNoticesAsync(1));
        agent.Chat.Calls(Get(1)).Waits().Answer("Jest jedno ogłoszenie.", 1);

        var asking = agent.AskAsync();
        await agent.Chat.Waiting.Task.WaitAsync(Ct);
        agent.Clock.Advance(TimeSpan.FromSeconds(121));
        var response = await asking;

        Assert.True(response.LimitReached);
        Assert.False(response.Refused);
        Assert.Equal(3, agent.Chat.Requests.Count);
    }

    [Fact]
    public async Task ToolCallInFlightAtTheTimeLimit_IsAbandoned_AndListedAsFailed()
    {
        var connection = await AgentTestHost.SeedAsync(database, [Notice(1)], new DocumentToolTests.Chunk("100001-2026", "swz-a", 1, "Wadium.", DocumentToolTests.Vector(1, 0)));
        await using var agent = new AgentTestHost(connection);
        var embedding = new TaskCompletionSource();
        agent.Generator.Transform = _ =>
        {
            // The embedding model does not answer until the clock has passed the limit.
            embedding.SetResult();
            SpinWait.SpinUntil(() => agent.Clock.GetUtcNow() > agent.Clock.Start.AddSeconds(120));
            Ct.WaitHandle.WaitOne(50);
            return [DocumentToolTests.Vector(1, 0)];
        };
        agent.Chat.Calls(Get(1), ("search_tender_documents", new { query = "wadium" })).Answer("Jest jedno ogłoszenie.", 1);

        var asking = agent.AskAsync();
        await embedding.Task.WaitAsync(Ct);
        agent.Clock.Advance(TimeSpan.FromSeconds(121));
        var response = await asking;

        Assert.True(response.LimitReached);
        Assert.Equal([("get_notice", false), ("search_tender_documents", true)], response.ToolCalls.Select(c => (c.Name, c.Failed)));
        Assert.Empty(response.Citations);
    }

    [Fact]
    public async Task LimitWithNothingGathered_IsARefusal_WithoutAnAnsweringCall()
    {
        await using var agent = new AgentTestHost(database.ConnectionString);
        agent.Chat.Calls(Missing(1)).Calls(Missing(2)).Calls(Missing(3)).Calls(Missing(4));

        var response = await agent.AskAsync();

        Assert.True(response.Refused);
        Assert.True(response.LimitReached);
        Assert.Equal(QuestionAnswerer.RefusalMessage, response.Answer);
        Assert.Equal(4, agent.Chat.Requests.Count);
        Assert.Equal(4, response.ToolCalls.Count);
    }

    [Fact]
    public async Task ModelThatCallsNoTool_IsRefused_AndItsOwnTextIsNotAnAnswer()
    {
        await using var agent = new AgentTestHost(database.ConnectionString);
        agent.Chat.Text("Wadium wynosi zwykle 3% wartości zamówienia.");

        var response = await agent.AskAsync();

        Assert.True(response.Refused);
        Assert.False(response.LimitReached);
        Assert.Empty(response.ToolCalls);
        Assert.Equal(QuestionAnswerer.RefusalMessage, response.Answer);
        Assert.Single(agent.Chat.Requests);
    }

    [Fact]
    public async Task AtMostTwelveToolCalls_AreRun()
    {
        await using var agent = new AgentTestHost(await SeedNoticesAsync(15));
        agent.Chat
            .Calls(Get(1), Get(2), Get(3), Get(4), Get(5))
            .Calls(Get(6), Get(7), Get(8), Get(9), Get(10))
            .Calls(Get(11), Get(12), Get(13), Get(14), Get(15))
            .Answer("Znalezione ogłoszenia.", 12);

        var response = await agent.AskAsync();

        Assert.True(response.LimitReached);
        Assert.Equal(12, response.ToolCalls.Count);
        Assert.Equal("100012-2026", Assert.Single(response.Notices).PublicationNumber);
        Assert.Equal(4, agent.Chat.Requests.Count);
        Assert.DoesNotContain("100013-2026", agent.Chat.Requests[3].Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallBeforeTheTwelfth_IsRun_WhenTheReplyAsksForMoreThanTwelve_AlsoWithToolsThatDoNotExist()
    {
        await using var agent = new AgentTestHost(await SeedNoticesAsync(1));
        agent.Chat.Calls([Get(1), .. Unknown(12)]).Answer("Jest jedno ogłoszenie.", 1);

        var response = await agent.AskAsync();

        Assert.True(response.LimitReached);
        Assert.Equal("100001-2026", Assert.Single(response.Notices).PublicationNumber);
        Assert.Equal(
            [("get_notice", false), .. Enumerable.Repeat(("no_such_tool", true), 11)],
            response.ToolCalls.Select(c => (c.Name, c.Failed)));
        Assert.Equal(2, agent.Chat.Requests.Count);
    }

    [Theory]
    [InlineData(12, false, 2)]
    [InlineData(13, true, 1)]
    public async Task CallsToToolsThatDoNotExist_CountTowardsTheTwelve(int asked, bool limitReached, int requests)
    {
        await using var agent = new AgentTestHost(database.ConnectionString);
        agent.Chat.Calls(Unknown(asked)).Text();

        var response = await agent.AskAsync();

        Assert.Equal(limitReached, response.LimitReached);
        Assert.True(response.Refused);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"{{\"i\":{i}}}"), response.ToolCalls.Select(c => c.Arguments?.GetRawText()));
        Assert.Equal(requests, agent.Chat.Requests.Count);
    }

    [Fact]
    public async Task ThirteenthCallInALaterReply_IsNotRun_AndEndsTheGathering()
    {
        await using var agent = new AgentTestHost(await SeedNoticesAsync(1));
        agent.Chat.Calls(Unknown(12)).Calls(Get(1));

        var response = await agent.AskAsync();

        Assert.True(response.LimitReached);
        Assert.True(response.Refused);
        Assert.Equal(12, response.ToolCalls.Count);
        Assert.Equal(2, agent.Chat.Requests.Count);
    }

    [Fact]
    public async Task AnsweringCall_ListsTheFirst25NoticesThenTheFirst12Passages_EachOnce()
    {
        // 40 notices and, in three tenders, 30 passages.
        var chunks = Enumerable.Range(1, 30)
            .Select(i => new DocumentToolTests.Chunk($"10000{(i % 3) + 1}-2026", $"swz-{i:00}", i, $"Wadium, punkt {i}.", DocumentToolTests.Vector(1, i / 100f)))
            .ToArray();
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [.. Enumerable.Range(1, 40).Select(Notice)], chunks));
        agent.Chat
            .Calls(("search_notices", new { limit = 25 }), ("search_notices", new { limit = 25, deadlineTo = "2026-12-15" }))
            .Calls(Documents(1), Documents(2), Documents(3), Documents(1))
            .Text()
            .Answer("Odpowiedź.", 1, 37, 38);

        var response = await agent.AskAsync();

        var sources = agent.Chat.Requests[3].Messages[1].Text.Split('\n').Where(line => line.StartsWith('[')).ToList();
        Assert.Equal(37, sources.Count);
        Assert.All(sources.Take(25), line => Assert.Contains("] ogłoszenie ", line, StringComparison.Ordinal));
        Assert.All(sources.Skip(25), line => Assert.Contains("] dokument ", line, StringComparison.Ordinal));
        Assert.Equal(37, sources.Distinct().Count());
        Assert.False(response.LimitReached);
        Assert.Equal((1, 1), (response.Notices.Count, response.Citations.Count));
    }

    [Fact]
    public async Task SameSourceFromTwoCalls_IsListedOnce_ButTwoChunksOfOnePageAreTwoPassages()
    {
        var connection = await AgentTestHost.SeedAsync(
            database,
            [Notice(1)],
            new DocumentToolTests.Chunk("100001-2026", "swz-a", 7, "Wadium wynosi 10 000 zł.", DocumentToolTests.Vector(1, 0)));
        await using (var dbContext = database.CreateDbContext(connection))
        {
            // A second chunk that starts on the same page of the same document.
            var document = dbContext.Documents.Single();
            dbContext.DocumentChunks.Add(new() { DocumentId = document.Id, Position = 1, PageNumber = 7, Text = "Wadium wnosi się przelewem.", Embedding = new Pgvector.Vector(DocumentToolTests.Vector(1, 0)), EmbeddingModel = "bge-m3" });
            await dbContext.SaveChangesAsync(Ct);
        }

        await using var agent = new AgentTestHost(connection);
        agent.Chat
            .Calls(("search_notices", new { }), Get(1), Documents(1), ("search_tender_documents", new { query = "wadium przelewem" }))
            .Text()
            .Answer("Odpowiedź.", 1, 2, 3);

        var response = await agent.AskAsync();

        Assert.Single(response.Notices);
        Assert.Equal(["Wadium wynosi 10 000 zł.", "Wadium wnosi się przelewem."], response.Citations.Select(c => c.Passage).Order(StringComparer.Ordinal).Reverse());
        Assert.Equal(3, agent.Chat.Requests[2].Messages[1].Text.Split('\n').Count(line => line.StartsWith('[')));
    }

    private static Notice Notice(int i) =>
        new($"{100000 + i}-2026", $"Ogłoszenie {i}", null, null, [], new DateOnly(2026, 11, 1).AddDays(-i), new DateOnly(2026, 12, 1).AddDays(i), null);

    private static (string Tool, object Arguments) Get(int i) => ("get_notice", new { publicationNumber = $"{100000 + i}-2026" });

    private static (string Tool, object Arguments)[] Unknown(int count) =>
        [.. Enumerable.Range(1, count).Select(i => ("no_such_tool", (object)new { i }))];

    private static (string Tool, object Arguments) Missing(int i) => ("get_notice", new { publicationNumber = $"{900000 + i}-2026" });

    private static (string Tool, object Arguments) Documents(int i) =>
        ("search_tender_documents", new { query = "wadium", publicationNumber = $"{100000 + i}-2026", limit = 10 });

    private Task<string> SeedNoticesAsync(int count) => AgentTestHost.SeedAsync(database, [.. Enumerable.Range(1, count).Select(Notice)]);
}
