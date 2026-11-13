using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Agent;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>The agent offers the model the tools of the MCP host and runs them in process.</summary>
public sealed class AgentToolsTests(PostgresFixture database)
{
    private static readonly Notice Servers = new(
        "111111-2026", "Dostawa serwerów", null, "48820000", ["48820000"], new DateOnly(2026, 11, 2), null, null);

    private static readonly DocumentToolTests.Chunk Deposit = new("111111-2026", "swz-a", 3, "Wadium wynosi 10 000 zł.", DocumentToolTests.Vector(1, 0));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Model_IsOfferedExactlyTheToolsOfTheMcpContract()
    {
        var contract = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Tools", "tool-contract.json"), Ct))!.AsArray();
        await using var agent = new AgentTestHost(database.ConnectionString);
        agent.Chat.Text();

        await agent.AskAsync();

        var offered = agent.Chat.Requests[0].Options!.Tools!.Cast<AIFunction>().ToList();
        Assert.Equal(contract.Select(t => (string)t!["name"]!).Order(), offered.Select(f => f.Name).Order());
        foreach (var function in offered)
        {
            var listed = new JsonObject
            {
                ["name"] = function.Name,
                ["description"] = function.Description,
                ["inputSchema"] = JsonNode.Parse(function.JsonSchema.GetRawText()),
            };
            Assert.True(JsonNode.DeepEquals(contract.Single(t => (string)t!["name"]! == function.Name), listed), $"{function.Name} differs from tool-contract.json. Offered: {listed}");
        }
    }

    [Fact]
    public async Task ToolResults_ReachTheModelAsTheJsonTheMcpHostReturns()
    {
        var connection = await AgentTestHost.SeedAsync(database, [Servers], Deposit);
        (string Tool, object Arguments)[] calls =
        [
            ("search_notices", new { }),
            ("search_notices", new { query = "nieistniejące" }),
            ("get_notice", new { publicationNumber = "111111-2026" }),
            ("get_notice", new { publicationNumber = "999999-2026" }),
            ("search_tender_documents", new { query = "wadium" }),
        ];
        await using var agent = new AgentTestHost(connection);
        agent.Chat.Calls(calls).Text().Answer("Odpowiedź.", 1);
        await using var mcp = await McpTestHost.StartAsync(connection, agent.Generator);

        await agent.AskAsync();

        for (var i = 0; i < calls.Length; i++)
        {
            var fromMcp = McpTestHost.Text(await mcp.CallAsync(calls[i].Tool, calls[i].Arguments));
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(fromMcp), JsonNode.Parse(agent.Chat.ToolResult($"call-{i + 1}"))),
                $"{calls[i].Tool}: the model got {agent.Chat.ToolResult($"call-{i + 1}")}, the MCP host returns {fromMcp}");
        }
    }

    [Fact]
    public async Task InvalidArguments_ReachTheModelAsTheToolsOwnMessage_AndItCanCorrectTheCall()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        agent.Chat
            .Calls(("search_notices", new { limit = 99 }))
            .Calls(("search_notices", new { limit = 5 }))
            .Text()
            .Answer("Jest jedno ogłoszenie.", 1);

        var response = await agent.AskAsync();

        Assert.Contains("limit must be between 1 and 25", agent.Chat.ToolResult("call-1"), StringComparison.Ordinal);
        Assert.Equal([true, false], response.ToolCalls.Select(c => c.Failed));
        Assert.Equal("111111-2026", Assert.Single(response.Notices).PublicationNumber);
    }

    [Fact]
    public async Task UnexpectedToolFailure_ReachesTheModelAsOneGeneralSentence_AndTheRunGoesOn()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers], Deposit));
        agent.Generator.Transform = _ => throw new InvalidOperationException("SELECT secret FROM models at 192.0.2.77");
        agent.Chat
            .Calls(("search_tender_documents", new { query = "wadium" }))
            .Calls(("get_notice", new { publicationNumber = "111111-2026" }))
            .Text()
            .Answer("Jest takie ogłoszenie.", 1);

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        Assert.Equal(JsonSerializer.Serialize(TenderAgent.ToolFailure), agent.Chat.ToolResult("call-1"));
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("192.0.2.77", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
        var answer = JsonSerializer.Deserialize<AgentResponse>(body, JsonSerializerOptions.Web)!;
        Assert.Equal([true, false], answer.ToolCalls.Select(c => c.Failed));
        Assert.False(answer.Refused);
    }

    [Fact]
    public async Task UnknownTool_AndUnreadableArguments_AreFailedCalls_AndTheRunGoesOn()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        agent.Chat
            .Calls(("get_tender_terms", new { publicationNumber = "111111-2026" }))
            .CallWithUnreadableArguments("get_notice")
            .Calls(("get_notice", new { publicationNumber = "111111-2026" }))
            .Text()
            .Answer("Jest takie ogłoszenie.", 1);

        var response = await agent.AskAsync();

        Assert.Contains("get_tender_terms", agent.Chat.ToolResult("call-1"), StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(TenderAgent.ToolFailure), agent.Chat.ToolResult("call-2"));
        Assert.Equal(
            [("get_tender_terms", true), ("get_notice", true), ("get_notice", false)],
            response.ToolCalls.Select(c => (c.Name, c.Failed)));
        Assert.Equal("111111-2026", response.ToolCalls[0].Arguments!.Value.GetProperty("publicationNumber").GetString());
        Assert.Null(response.ToolCalls[1].Arguments);
        Assert.False(response.LimitReached);
    }
}
