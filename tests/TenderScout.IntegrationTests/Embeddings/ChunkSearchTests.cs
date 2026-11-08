using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Embeddings;

public sealed class ChunkSearchTests(PostgresFixture database)
{
    private const string Model = "bge-m3";

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Search_FixedVectors_ReturnsNearestInOrderWithOrigin()
    {
        var connection = await SeedAsync(
            new("doc-1", 2, "A", Vector(1, 0)),
            new("doc-2", 5, "B", Vector(0.8f, 0.6f)),
            new("doc-1", 9, "C", Vector(0, 1)),
            new("doc-2", 1, "D", Vector(-1, 0)));

        var results = await SearchAsync(connection, Vector(1, 0), limit: 3);

        Assert.Equal(["A", "B", "C"], results.Select(r => r.Text));
        Assert.Equal(["doc-1", "doc-2", "doc-1"], results.Select(r => r.DocId));
        Assert.Equal([2, 5, 9], results.Select(r => r.PageNumber));
        Assert.Equal(1.0, results[0].Score, 5);
        Assert.Equal(0.8, results[1].Score, 5);
        Assert.Equal(0.0, results[2].Score, 5);
    }

    [Fact]
    public async Task Search_OrdersByCosineRatherThanEuclideanDistance()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Offset", Vector(1, 0.5f)),
            new("doc-1", 2, "Collinear", Vector(10, 10)));

        var results = await SearchAsync(connection, Vector(1, 1), limit: 2);

        Assert.Equal(["Collinear", "Offset"], results.Select(r => r.Text));
    }

    [Fact]
    public async Task Search_ScoreIsCosineSimilarity()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "V", Vector(1, 0)),
            new("doc-1", 2, "W", Vector(0.5f, MathF.Sqrt(3) / 2)));

        var results = await SearchAsync(connection, Vector(1, 0), limit: 2);

        Assert.Equal(1.0, results[0].Score, 5);
        Assert.Equal(0.5, results[1].Score, 5);
    }

    [Fact]
    public async Task Search_LeavesOutChunksWithoutAnEmbedding()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "With 1", Vector(1, 0)),
            new("doc-1", 2, "With 2", Vector(0, 1)),
            new("doc-1", 3, "Without 1", null),
            new("doc-1", 4, "Without 2", null));

        var results = await SearchAsync(connection, Vector(1, 0), limit: 10);

        Assert.Equal(["With 1", "With 2"], results.Select(r => r.Text));
    }

    [Fact]
    public async Task Search_ReturnsOnlyTheRequestedModel()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Same model", Vector(1, 0)),
            new("doc-1", 2, "Other model", Vector(1, 0), "other-model"));

        var results = await SearchAsync(connection, Vector(1, 0), limit: 5);

        Assert.Equal("Same model", Assert.Single(results).Text);
    }

    [Fact]
    public async Task Search_NoMatchingChunks_ReturnsEmpty()
    {
        var connection = await SeedAsync(new Seed("doc-1", 1, "Other", Vector(1, 0), "other-model"));

        Assert.Empty(await SearchAsync(connection, Vector(1, 0), limit: 5));
    }

    private static float[] Vector(float first, float second)
    {
        var vector = new float[ChunkEmbedding.Dimensions];
        vector[0] = first;
        vector[1] = second;
        return vector;
    }

    private async Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(string connection, float[] query, int limit)
    {
        await using var dbContext = database.CreateDbContext(connection);
        return await new ChunkEmbeddingRepository(dbContext).SearchAsync(query, Model, limit, Ct);
    }

    private async Task<string> SeedAsync(params Seed[] seeds)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);

        foreach (var group in seeds.GroupBy(seed => seed.DocId))
        {
            var document = DocumentEntity.Create(new TenderDocument(group.Key, "100001-2026", "swz", "a.pdf", new string('a', 64), null), Now);
            document.Chunks.AddRange(group.Select((seed, position) => new DocumentChunkEntity
            {
                Position = position,
                PageNumber = seed.Page,
                Text = seed.Text,
                Embedding = seed.Vector is null ? null : new Pgvector.Vector(seed.Vector),
                EmbeddingModel = seed.Vector is null ? null : seed.Model,
            }));
            dbContext.Documents.Add(document);
        }

        await dbContext.SaveChangesAsync(Ct);
        return connection;
    }

    private sealed record Seed(string DocId, int Page, string Text, float[]? Vector, string Model = Model);
}
