using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Tools;

/// <summary>
/// What every MCP client relies on: the list of tools with their descriptions and schemas, the form of results and
/// errors, and the HTTP endpoint. <c>tool-contract.json</c> is written by hand; a changed tool fails here.
/// </summary>
public sealed class ToolContractTests(PostgresFixture database)
{
    private const string Question = "Jaka jest wysokość wadium?";

    private readonly FakeEmbeddingGenerator _generator = new() { Transform = _ => [DocumentToolTests.Vector(1, 0)] };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListedTools_AreExactlyTheContractInTheFile()
    {
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Tools", "tool-contract.json"), Ct))!.AsArray();
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString);

        var tools = await mcp.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(expected.Select(t => (string)t!["name"]!).Order(), tools.Select(t => t.Name).Order());
        foreach (var tool in tools)
        {
            var listed = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
            };
            var contract = expected.Single(t => (string)t!["name"]! == tool.Name);
            Assert.True(JsonNode.DeepEquals(contract, listed), $"{tool.Name} differs from tool-contract.json. Listed: {listed}");
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        }
    }

    [Fact]
    public async Task EveryResult_IsStructuredContentAndTheSameJsonAsText()
    {
        await using var mcp = await McpTestHost.StartAsync(await SeedAsync(), _generator);
        (string Tool, object Arguments)[] calls =
        [
            ("search_notices", new { }),
            ("search_notices", new { query = "nieistniejące" }),
            ("get_notice", new { publicationNumber = "111111-2026" }),
            ("get_notice", new { publicationNumber = "999999-2026" }),
            ("search_tender_documents", new { query = Question }),
            ("search_tender_documents", new { query = Question, publicationNumber = "999999-2026" }),
        ];

        foreach (var (tool, arguments) in calls)
        {
            var result = await mcp.CallAsync(tool, arguments);

            Assert.True(result.IsError is not true);
            var structured = JsonSerializer.SerializeToNode(result.StructuredContent);
            Assert.True(JsonNode.DeepEquals(structured, JsonNode.Parse(McpTestHost.Text(result))), $"{tool}: text and structured content differ.");
        }
    }

    [Fact]
    public async Task UnknownTool_IsAProtocolError_BadArgumentsAreToolErrors_AndTheServerKeepsAnswering()
    {
        await using var mcp = await McpTestHost.StartAsync(await SeedAsync(), _generator);

        await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync("get_tender_terms"));
        await mcp.CallForErrorAsync("get_notice");
        await mcp.CallForErrorAsync("search_tender_documents", new { limit = 3 });
        await mcp.CallForErrorAsync("search_notices", new { limit = "ten" });
        await mcp.CallForErrorAsync("search_notices", new { limit = (int?)null });

        Assert.Empty(_generator.Calls);
        Assert.True((await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = "111111-2026" })).Found);
    }

    [Fact]
    public async Task UnexpectedFailure_IsAnErrorWithoutDetails_IsLogged_AndTheNextCallWorks()
    {
        _generator.Transform = _ => throw new InvalidOperationException("SELECT secret FROM models at 192.0.2.77");
        await using var mcp = await McpTestHost.StartAsync(await SeedAsync(), _generator);

        var error = await mcp.CallForErrorAsync("search_tender_documents", new { query = Question });

        Assert.DoesNotContain("192.0.2.77", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
        Assert.Contains(mcp.Errors, log => log.Contains("192.0.2.77", StringComparison.Ordinal));
        _generator.Transform = _ => [DocumentToolTests.Vector(1, 0)];
        Assert.Single((await mcp.CallAsync<PassageSearchResult>("search_tender_documents", new { query = Question })).Passages);
    }

    [Fact]
    public async Task CancelledCall_EndsWithoutAResultOrAnErrorLog_AndTheNextCallWorks()
    {
        var waiting = new WaitingGenerator();
        await using var mcp = await McpTestHost.StartAsync(await SeedAsync(), waiting);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = mcp.Client.CallToolAsync("search_tender_documents", new Dictionary<string, object?> { ["query"] = Question }, cancellationToken: cancellation.Token);
        await waiting.Started.Task.WaitAsync(Ct);
        await cancellation.CancelAsync();

        // How the client reports its own cancellation is the SDK's business; the call must end without a result.
        await Assert.ThrowsAnyAsync<Exception>(async () => await call);
        await waiting.Cancelled.Task.WaitAsync(Ct);
        Assert.True((await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = "111111-2026" })).Found);
        Assert.Empty(mcp.Errors);
    }

    [Fact]
    public async Task Tools_ChangeNothingInTheDatabase()
    {
        var connection = await SeedAsync();
        await using var dbContext = database.CreateDbContext(connection);
        var before = await SnapshotAsync(dbContext);
        await using var mcp = await McpTestHost.StartAsync(connection, _generator);

        await mcp.CallAsync("search_notices", new { query = "Dostawa", cpvPrefix = "72" });
        await mcp.CallAsync("get_notice", new { publicationNumber = "111111-2026" });
        await mcp.CallAsync("get_notice", new { publicationNumber = "abc" });
        await mcp.CallAsync("search_tender_documents", new { query = Question });
        await mcp.CallAsync("search_tender_documents", new { query = " " });

        Assert.Equal(before, await SnapshotAsync(dbContext));
    }

    [Fact]
    public async Task Host_IsHealthyAndListsItsTools_WithTheEmbeddingModelUnreachable()
    {
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString);
        using var http = mcp.CreateHttpClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/alive", Ct)).StatusCode);
        Assert.Equal(3, (await mcp.Client.ListToolsAsync(cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task TwoClients_WorkIndependently_AndThereIsNoLegacySseEndpoint()
    {
        await using var mcp = await McpTestHost.StartAsync(await SeedAsync(), _generator);
        await using var second = await mcp.ConnectAsync();
        using var http = mcp.CreateHttpClient();

        var result = await second.CallToolAsync("get_notice", new Dictionary<string, object?> { ["publicationNumber"] = "111111-2026" }, cancellationToken: Ct);

        Assert.True(result.IsError is not true);
        Assert.True((await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = "111111-2026" })).Found);
        Assert.Null(second.SessionId);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/sse", Ct)).StatusCode);
    }

    /// <summary>A database with one notice and one chunk of the same tender.</summary>
    private async Task<string> SeedAsync()
    {
        var connection = await DocumentToolTests.SeedAsync(
            database,
            new DocumentToolTests.Chunk("111111-2026", "swz-a", 3, "Wadium wynosi 10 000 zł.", DocumentToolTests.Vector(1, 0)));
        await using var dbContext = database.CreateDbContext(connection);
        dbContext.Notices.Add(NoticeEntity.Create(
            new Notice("111111-2026", "Dostawa serwerów", null, "72000000", ["72000000"], new DateOnly(2026, 11, 2), null, null),
            DateTimeOffset.UnixEpoch));
        await dbContext.SaveChangesAsync(Ct);
        return connection;
    }

    /// <summary>A checksum of every row of the three tables the tools read.</summary>
    internal static Task<string> SnapshotAsync(TenderScoutDbContext dbContext) =>
        dbContext.Database
            .SqlQuery<string>($"""
                SELECT (SELECT md5(coalesce(string_agg(t::text, '|' ORDER BY t.id), '')) FROM notices t)
                    || (SELECT md5(coalesce(string_agg(t::text, '|' ORDER BY t.id), '')) FROM documents t)
                    || (SELECT md5(coalesce(string_agg(t::text, '|' ORDER BY t.id), '')) FROM document_chunks t) AS "Value"
                """)
            .SingleAsync(Ct);

    /// <summary>An embedding model that answers only when the call is cancelled.</summary>
    private sealed class WaitingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public TaskCompletionSource Started { get; } = new();

        public TaskCompletionSource Cancelled { get; } = new();

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Cancelled.SetResult();
            }

            return [];
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
