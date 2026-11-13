using Polly.Timeout;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Tools;

/// <summary>
/// <c>search_tender_documents</c> through an MCP client. Chunk vectors are fixed and the fake generator returns the
/// query vector the test sets, so the ranking is known.
/// </summary>
public sealed class DocumentToolTests(PostgresFixture database)
{
    private const string Question = "Jaka jest wysokość wadium?";

    private readonly FakeEmbeddingGenerator _generator = new() { Transform = _ => [Vector(1, 0)] };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Passages_AreWhatHybridSearchReturns_InItsOrder()
    {
        var connection = await SeedAsync(database, [.. Enumerable.Range(1, 7).Select(i =>
            new Chunk(i % 2 == 0 ? "111111-2026" : "222222-2026", $"swz-{i}", i, $"Wadium wynosi {i} 000 zł.", Vector(1, i / 10f)))]);
        await using var mcp = await McpTestHost.StartAsync(connection, _generator);

        var passages = (await mcp.CallAsync<PassageSearchResult>("search_tender_documents", new { query = $"  {Question} " })).Passages;

        await using var dbContext = database.CreateDbContext(connection);
        var expected = await new ChunkEmbeddingRepository(dbContext).HybridSearchAsync(Question, Vector(1, 0), "bge-m3", 5, cancellationToken: Ct);
        Assert.Equal(5, passages.Count);
        Assert.Equal(expected.Select(c => new Passage(c.DocId, c.PublicationNumber, c.PageNumber, c.Text)), passages);
        Assert.Equal([Question], Assert.Single(_generator.Calls));
    }

    [Fact]
    public async Task ChunkFoundOnlyByTextOrOnlyByVector_IsReturned()
    {
        var connection = await SeedAsync(
            database,
            new Chunk("111111-2026", "swz-code", 2, "Kod CPV 72212460-1 dotyczy usług programowania.", Vector: null),
            new Chunk("111111-2026", "swz-meaning", 3, "Zabezpieczenie oferty wnosi się przelewem.", Vector(0, 1)),
            new Chunk("111111-2026", "swz-other", 4, "Termin wykonania zamówienia: 30 dni.", Vector(1, 0)));
        await using var mcp = await McpTestHost.StartAsync(connection, _generator);

        var byText = await SearchAsync(mcp, new { query = "72212460" });
        _generator.Transform = _ => [Vector(0, 1)];
        var byVector = await SearchAsync(mcp, new { query = "kaucja" });

        Assert.Equal("swz-code", byText[0].DocId);
        Assert.Equal("swz-meaning", byVector[0].DocId);
    }

    [Fact]
    public async Task PublicationNumber_LimitsTheSearchToThatTender()
    {
        var connection = await SeedAsync(
            database,
            new Chunk("111111-2026", "swz-a", 1, "Wadium wynosi 10 000 zł.", Vector(1, 0)),
            new Chunk("222222-2026", "swz-b", 2, "Wadium wynosi 20 000 zł.", Vector(0.8f, 0.2f)));
        await using var mcp = await McpTestHost.StartAsync(connection, _generator);

        Assert.Equal(["swz-b"], (await SearchAsync(mcp, new { query = Question, publicationNumber = " 222222-2026 " })).Select(p => p.DocId));
        Assert.Equal(2, (await SearchAsync(mcp, new { query = Question, publicationNumber = (string?)null })).Count);
        Assert.Empty(await SearchAsync(mcp, new { query = Question, publicationNumber = "333333-2026" }));
    }

    [Fact]
    public async Task Limit_CapsThePassages()
    {
        var connection = await SeedAsync(database, [.. Enumerable.Range(1, 12).Select(i =>
            new Chunk("111111-2026", $"swz-{i:00}", i, $"Wadium, punkt {i}.", Vector(1, i / 100f)))]);
        await using var mcp = await McpTestHost.StartAsync(connection, _generator);

        Assert.Equal(5, (await SearchAsync(mcp, new { query = Question })).Count);
        Assert.Equal(["swz-01"], (await SearchAsync(mcp, new { query = Question, limit = 1 })).Select(p => p.DocId));
        Assert.Equal(10, (await SearchAsync(mcp, new { query = Question, limit = 10 })).Count);
    }

    [Fact]
    public async Task DistantPassage_IsReturnedWhole_WhereAskWouldRefuse()
    {
        var text = "Zamawiający wymaga:\n  1) polisy OC,\n  2) referencji.\n" + new string('ż', 1100);
        var connection = await SeedAsync(database, new Chunk("111111-2026", "swz-a", 9, text, Vector(0, 1)));
        await using var mcp = await McpTestHost.StartAsync(connection, _generator);

        var passage = Assert.Single(await SearchAsync(mcp, new { query = Question }));

        Assert.Equal(new Passage("swz-a", "111111-2026", 9, text), passage);
    }

    [Fact]
    public async Task EmptyDatabase_IsAnEmptyList()
    {
        await using var mcp = await McpTestHost.StartAsync(await database.CreateIsolatedDatabaseAsync(), _generator);

        Assert.Empty(await SearchAsync(mcp, new { query = Question }));
    }

    [Theory]
    [InlineData("   ", null, 5, "query")]
    [InlineData(Question, "123456/2026", 5, "publicationNumber")]
    [InlineData(Question, null, 11, "limit")]
    public async Task InvalidInput_IsAnErrorThatNamesTheParameter_BeforeTheModelIsCalled(
        string query, string? publicationNumber, int limit, string parameter)
    {
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString, _generator);

        var error = await mcp.CallForErrorAsync("search_tender_documents", new { query, publicationNumber, limit });

        Assert.Contains(parameter, error, StringComparison.Ordinal);
        Assert.Empty(_generator.Calls);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("http status")]
    [InlineData("timeout")]
    [InlineData("pipeline timeout")]
    public async Task UnreachableModel_IsAnErrorWithoutDetails_AndTheNoticeToolsStillWork(string failure)
    {
        Exception exception = failure switch
        {
            "connection" => new HttpRequestException("Connection refused (http://192.0.2.99:11434/api/embed)"),
            "http status" => new HttpRequestException("model 'secret-model' not found", null, System.Net.HttpStatusCode.NotFound),
            "timeout" => new TaskCanceledException("The request to 192.0.2.99 was canceled due to the configured timeout."),
            _ => new TimeoutRejectedException("The operation at 192.0.2.99 didn't complete within the allowed timeout."),
        };
        _generator.Transform = _ => throw exception;
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString, _generator);

        var error = await mcp.CallForErrorAsync("search_tender_documents", new { query = Question });

        Assert.Contains("embedding model is unavailable", error, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.99", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-model", error, StringComparison.Ordinal);
        Assert.False((await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = "999999-2026" })).Found);
        await mcp.CallAsync<NoticeSearchResult>("search_notices", new { limit = 1 });
    }

    internal static float[] Vector(float first, float second)
    {
        var vector = new float[ChunkEmbedding.Dimensions];
        vector[0] = first;
        vector[1] = second;
        return vector;
    }

    /// <summary>Stores the chunks in a new database, one document per document id.</summary>
    internal static async Task<string> SeedAsync(PostgresFixture database, params Chunk[] chunks)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);
        foreach (var chunk in chunks)
        {
            var document = DocumentEntity.Create(
                new TenderDocument(chunk.DocId, chunk.PublicationNumber, "swz", "a.pdf", new string('a', 64), null),
                DateTimeOffset.UnixEpoch);
            document.Chunks.Add(new DocumentChunkEntity
            {
                Position = 0,
                PageNumber = chunk.Page,
                Text = chunk.Text,
                Embedding = chunk.Vector is null ? null : new Pgvector.Vector(chunk.Vector),
                EmbeddingModel = chunk.Vector is null ? null : "bge-m3",
            });
            dbContext.Documents.Add(document);
        }

        await dbContext.SaveChangesAsync(Ct);
        return connection;
    }

    private static async Task<IReadOnlyList<Passage>> SearchAsync(McpTestHost mcp, object arguments) =>
        (await mcp.CallAsync<PassageSearchResult>("search_tender_documents", arguments)).Passages;

    internal sealed record Chunk(string PublicationNumber, string DocId, int Page, string Text, float[]? Vector);
}
