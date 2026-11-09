using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Embeddings;

public sealed class DocumentSearchCommandTests(PostgresFixture database)
{
    private const string Model = "bge-m3";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmbeddingGenerator _generator = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankQuery_PrintsUsageAndFailsWithoutCallingTheGenerator(string query)
    {
        var exitCode = await RunAsync(database.ConnectionString, query);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Usage:", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_generator.Calls);
    }

    [Fact]
    public async Task GeneratorFailure_IsPrintedAndFailsTheCommand()
    {
        _generator.FailOnCall = 1;

        var exitCode = await RunAsync(database.ConnectionString, "Jaka jest wysokość wadium?");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Connection refused", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public async Task EmbeddedChunks_PrintsTheBestFiveWithDocumentPageAndScore()
    {
        var connection = await SeedAsync(
            ("doc-1", 2, "Fragment A", Vector(1, 0), Model),
            ("doc-2", 5, "Fragment B", Vector(0.9f, 0.1f), Model),
            ("doc-1", 7, "Fragment C", Vector(0.8f, 0.2f), Model),
            ("doc-2", 9, "Fragment D", Vector(0.7f, 0.3f), Model),
            ("doc-1", 11, "Fragment E", Vector(0.6f, 0.4f), Model),
            ("doc-2", 13, "Fragment F", Vector(0.1f, 0.9f), Model),
            ("doc-1", 1, "Inny model", Vector(1, 0), "other-model"));
        _generator.Transform = _ => [Vector(1, 0)];

        var exitCode = await RunAsync(connection, "Jaka jest wysokość wadium?");

        Assert.Equal(0, exitCode);
        var printed = _output.ToString();
        Assert.StartsWith("1. score 1.000 | doc-1 | page 2", printed, StringComparison.Ordinal);
        var positions = new[] { "Fragment A", "Fragment B", "Fragment C", "Fragment D", "Fragment E" }
            .Select(text => printed.IndexOf(text, StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
        Assert.DoesNotContain("Fragment F", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("Inny model", printed, StringComparison.Ordinal);
        Assert.Equal(["Jaka jest wysokość wadium?"], _generator.Calls.Single());
    }

    [Fact]
    public async Task NoEmbeddedChunks_SaysSoAndSucceeds()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();

        var exitCode = await RunAsync(connection, "Jaka jest wysokość wadium?");

        Assert.Equal(0, exitCode);
        Assert.Contains("No matching passages", _output.ToString(), StringComparison.Ordinal);
    }

    private static float[] Vector(float first, float second)
    {
        var vector = new float[ChunkEmbedding.Dimensions];
        vector[0] = first;
        vector[1] = second;
        return vector;
    }

    private async Task<int> RunAsync(string connection, string query)
    {
        await using var dbContext = database.CreateDbContext(connection);
        var options = Options.Create(new EmbeddingOptions { Endpoint = "http://ollama.test:11434", Model = Model });
        var command = new DocumentSearchCommand(_generator, new ChunkEmbeddingRepository(dbContext), options);
        return await command.RunAsync(query, _output, _error, Ct);
    }

    private async Task<string> SeedAsync(params (string DocId, int Page, string Text, float[] Vector, string Model)[] seeds)
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
                Embedding = new Pgvector.Vector(seed.Vector),
                EmbeddingModel = seed.Model,
            }));
            dbContext.Documents.Add(document);
        }

        await dbContext.SaveChangesAsync(Ct);
        return connection;
    }
}
