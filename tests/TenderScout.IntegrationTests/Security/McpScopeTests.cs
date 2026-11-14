using ModelContextProtocol;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Tools;
using TenderScout.IntegrationTests.Agent;
using TenderScout.IntegrationTests.Tools;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Security;

/// <summary>
/// The MCP server with a token that has one of the two scopes: the connection works, the tool list holds the
/// tools of that scope, and a tool of the other scope is refused by the protocol without being run.
/// </summary>
public sealed class McpScopeTests(PostgresFixture database)
{
    private const string Number = "940222-2026";

    private static readonly Notice Stored = new(Number, "Dostawa ambulansów", null, null, [], new DateOnly(2026, 11, 3), null, null);

    private static readonly DocumentToolTests.Chunk Passage = new(Number, "swz-a", 4, "Wadium wynosi 12 000 zł.", DocumentToolTests.Vector(1, 0));

    private readonly FakeEmbeddingGenerator _generator = new() { Transform = _ => [DocumentToolTests.Vector(1, 0)] };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoticesScope_ListsAndRunsTheNoticeTools_AndIsRefusedTheDocumentSearch()
    {
        await using var mcp = await McpTestHost.StartAsync(await AgentTestHost.SeedAsync(database, [Stored], Passage), _generator, Scopes.NoticesRead);

        var tools = await mcp.Client.ListToolsAsync(cancellationToken: Ct);
        var found = await mcp.CallAsync<NoticeSearchResult>("search_notices", new { query = "ambulans" });
        var read = await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = Number });
        var refused = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync("search_tender_documents", new { query = "wadium" }));

        Assert.Equal(["get_notice", "search_notices"], tools.Select(tool => tool.Name).Order());
        Assert.Equal(Number, Assert.Single(found.Notices).PublicationNumber);
        Assert.True(read.Found);
        Assert.Contains("forbidden", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_generator.Calls);
    }

    [Fact]
    public async Task DocumentsScope_ListsAndRunsTheDocumentSearch_AndIsRefusedTheNoticeTools()
    {
        await using var mcp = await McpTestHost.StartAsync(await AgentTestHost.SeedAsync(database, [Stored], Passage), _generator, Scopes.DocumentsAsk);

        var tools = await mcp.Client.ListToolsAsync(cancellationToken: Ct);
        var found = await mcp.CallAsync<PassageSearchResult>("search_tender_documents", new { query = "wadium" });
        var search = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync("search_notices"));
        var read = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync("get_notice", new { publicationNumber = Number }));

        Assert.Equal("search_tender_documents", Assert.Single(tools).Name);
        Assert.Equal("Wadium wynosi 12 000 zł.", Assert.Single(found.Passages).Text);
        Assert.All([search, read], refused => Assert.Contains("forbidden", refused.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BothScopes_ListAllThreeTools()
    {
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString);

        var tools = await mcp.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(["get_notice", "search_notices", "search_tender_documents"], tools.Select(tool => tool.Name).Order());
    }
}
