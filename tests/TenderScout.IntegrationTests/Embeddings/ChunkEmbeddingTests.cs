using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pgvector;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Embeddings;

/// <summary>Each test owns a database, because a pass embeds every pending chunk it can see.</summary>
public sealed class ChunkEmbeddingTests(PostgresFixture database)
{
    private const string Model = "bge-m3";
    private const string MigrationBeforeEmbeddings = "20261108155731_AddDocuments";

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmbeddingGenerator _generator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_InstallsExtensionColumnAndCosineHnswIndex()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);

        var extensions = await dbContext.Database
            .SqlQuery<string>($"SELECT extname::text AS \"Value\" FROM pg_extension")
            .ToListAsync(Ct);
        var columnType = await dbContext.Database
            .SqlQuery<string>($"""
                SELECT format_type(a.atttypid, a.atttypmod) AS "Value"
                FROM pg_attribute a
                WHERE a.attrelid = 'document_chunks'::regclass AND a.attname = 'embedding' AND NOT a.attnotnull
                """)
            .SingleAsync(Ct);
        var index = await dbContext.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_document_chunks_embedding_hnsw'")
            .SingleAsync(Ct);

        Assert.Contains("vector", extensions);
        Assert.Equal("vector(1024)", columnType);
        Assert.Contains("USING hnsw", index, StringComparison.Ordinal);
        Assert.Contains("vector_cosine_ops", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_KeepsExistingChunksWithoutEmbeddings()
    {
        var connection = await database.CreateIsolatedDatabaseAsync(MigrationBeforeEmbeddings);
        await using (var before = database.CreateDbContext(connection))
        {
            await before.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO documents (doc_id, publication_number, kind, path, sha256, created_at, updated_at)
                VALUES ('doc-old', '100001-2026', 'swz', 'a.pdf', {new string('a', 64)}, {Now}, {Now})
                """,
                Ct);
            await before.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO document_chunks (document_id, position, page_number, text)
                SELECT id, 0, 3, 'Tresc sprzed migracji' FROM documents WHERE doc_id = 'doc-old'
                """,
                Ct);
        }

        await using var after = database.CreateDbContext(connection);
        await after.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);

        var chunk = await after.DocumentChunks.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(3, chunk.PageNumber);
        Assert.Equal("Tresc sprzed migracji", chunk.Text);
        Assert.Null(chunk.Embedding);
        Assert.Null(chunk.EmbeddingModel);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(512)]
    public async Task Column_RejectsAnotherDimension(int dimensions)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var ids = await SeedAsync(connection, ("doc-1", 1));
        await using var dbContext = database.CreateDbContext(connection);
        var wrong = new Vector(new float[dimensions]);

        var exception = await Assert.ThrowsAnyAsync<DbException>(() => dbContext.DocumentChunks
            .Where(c => c.Id == ids[0])
            .ExecuteUpdateAsync(update => update.SetProperty(c => c.Embedding, wrong), Ct));

        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_StoresEveryValueAndTheModelName()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 3), ("doc-2", 2));

        var report = await EmbedAsync(connection, batchSize: 2);

        Assert.Equal(new ChunkEmbeddingReport(5, 3), report);
        await using var dbContext = database.CreateDbContext(connection);
        var chunks = await dbContext.DocumentChunks.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(5, chunks.Count);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(Model, chunk.EmbeddingModel);
            Assert.Equal(FakeEmbeddingGenerator.VectorFor(chunk.Text), chunk.Embedding!.ToArray());
        });
    }

    [Fact]
    public async Task Run_SplitsChunksIntoConfiguredBatchesInIdOrder()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 7));

        var report = await EmbedAsync(connection, batchSize: 3);

        Assert.Equal(new ChunkEmbeddingReport(7, 3), report);
        Assert.Equal([3, 3, 1], _generator.Calls.Select(call => call.Count));
        await using var dbContext = database.CreateDbContext(connection);
        var texts = await dbContext.DocumentChunks.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Text).ToListAsync(Ct);
        Assert.Equal(texts, _generator.Calls.SelectMany(call => call));
    }

    [Fact]
    public async Task Run_TooFewVectors_StoresNothingFromThatBatch()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 3));
        _generator.Transform = vectors => [.. vectors.Take(2)];

        var exception = await Assert.ThrowsAsync<ChunkEmbeddingException>(() => EmbedAsync(connection));

        Assert.Contains("expected 3 vectors, got 2", exception.Message, StringComparison.Ordinal);
        await AssertNothingStoredAsync(connection);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1536)]
    public async Task Run_WrongDimension_NamesBothLengthsAndStoresNothing(int dimensions)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 2));
        _generator.Transform = vectors => [.. vectors.Select(_ => new float[dimensions])];

        var exception = await Assert.ThrowsAsync<ChunkEmbeddingException>(() => EmbedAsync(connection));

        Assert.Contains($"expected 1024 dimensions, got {dimensions}", exception.Message, StringComparison.Ordinal);
        await AssertNothingStoredAsync(connection);
    }

    [Fact]
    public async Task Run_CancelledWhileGenerating_IsNotReportedAsAGeneratorFailure()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 1));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _generator.Transform = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EmbedAsync(connection, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Run_SecondTime_SendsNothingAndChangesNothing()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 5));
        await EmbedAsync(connection);
        _generator.Calls.Clear();
        var before = await ReadVectorsAsync(connection);

        var report = await EmbedAsync(connection);

        Assert.Equal(new ChunkEmbeddingReport(0, 0), report);
        Assert.Empty(_generator.Calls);
        Assert.Equal(before, await ReadVectorsAsync(connection));
    }

    [Fact]
    public async Task Run_InterruptedByFailure_KeepsFinishedBatchesAndResumes()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 5));
        _generator.FailOnCall = 3;

        var exception = await Assert.ThrowsAsync<ChunkEmbeddingException>(() => EmbedAsync(connection, batchSize: 2));

        Assert.Contains("Batch 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Connection refused", exception.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(3, _generator.Calls.Count);
        await using (var dbContext = database.CreateDbContext(connection))
        {
            Assert.Equal(4, await dbContext.DocumentChunks.CountAsync(c => c.Embedding != null, Ct));
        }

        _generator.FailOnCall = null;
        _generator.Calls.Clear();
        var report = await EmbedAsync(connection, batchSize: 2);

        Assert.Equal(new ChunkEmbeddingReport(1, 1), report);
        Assert.Single(_generator.Calls.Single());
    }

    [Fact]
    public async Task Run_ChunksFromAnotherModel_AreEmbeddedAgain()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var ids = await SeedAsync(connection, ("doc-1", 3));
        await SetEmbeddingAsync(connection, ids[0], Vector(1), "old-model");
        await SetEmbeddingAsync(connection, ids[1], Vector(1), "old-model");
        await SetEmbeddingAsync(connection, ids[2], Vector(1), Model);

        var report = await EmbedAsync(connection);

        Assert.Equal(2, report.Embedded);
        Assert.Equal(2, _generator.Calls.Single().Count);
        await using var dbContext = database.CreateDbContext(connection);
        Assert.Equal(3, await dbContext.DocumentChunks.CountAsync(c => c.EmbeddingModel == Model, Ct));
    }

    [Fact]
    public async Task Run_WithRetryOnFailureEnabled_StoresVectors()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SeedAsync(connection, ("doc-1", 2));

        var report = await EmbedAsync(connection, retryOnFailure: true);

        Assert.Equal(2, report.Embedded);
    }

    [Fact]
    public async Task Save_ChunkDeletedInTheMeantime_IsNotAnError()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var ids = await SeedAsync(connection, ("doc-1", 1));
        await using var dbContext = database.CreateDbContext(connection);
        var repository = new ChunkEmbeddingRepository(dbContext);

        await repository.SaveAsync(Model, [new ChunkEmbedding(ids[0] + 1000, Vector(1)), new ChunkEmbedding(ids[0], Vector(1))], Ct);

        Assert.Equal(Model, (await dbContext.DocumentChunks.AsNoTracking().SingleAsync(Ct)).EmbeddingModel);
    }

    [Fact]
    public async Task Save_FailureInsideABatch_StoresNoneOfIt()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var ids = await SeedAsync(connection, ("doc-1", 2));
        await using var dbContext = database.CreateDbContext(connection);
        var repository = new ChunkEmbeddingRepository(dbContext);

        await Assert.ThrowsAnyAsync<DbException>(() => repository.SaveAsync(
            Model,
            [new ChunkEmbedding(ids[0], Vector(1)), new ChunkEmbedding(ids[1], new float[3])],
            Ct));

        Assert.Equal(0, await dbContext.DocumentChunks.CountAsync(c => c.Embedding != null, Ct));
    }

    [Fact]
    public async Task NewVersionOfADocument_ResetsOnlyThatDocument()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await SaveDocumentAsync(connection, "doc-a", 'a', "Pierwsza wersja A");
        await SaveDocumentAsync(connection, "doc-b", 'b', "Dokument B");
        await EmbedAsync(connection);
        _generator.Calls.Clear();

        await SaveDocumentAsync(connection, "doc-a", 'c', "Druga wersja A");

        await using (var dbContext = database.CreateDbContext(connection))
        {
            var a = await dbContext.Documents.AsNoTracking().Include(d => d.Chunks).SingleAsync(d => d.DocId == "doc-a", Ct);
            var b = await dbContext.Documents.AsNoTracking().Include(d => d.Chunks).SingleAsync(d => d.DocId == "doc-b", Ct);
            Assert.All(a.Chunks, chunk => Assert.Null(chunk.Embedding));
            Assert.All(b.Chunks, chunk => Assert.NotNull(chunk.Embedding));
        }

        var report = await EmbedAsync(connection);

        Assert.Equal(1, report.Embedded);
        Assert.Equal("Druga wersja A", _generator.Calls.Single().Single());
    }

    private static float[] Vector(float first)
    {
        var vector = new float[ChunkEmbedding.Dimensions];
        vector[0] = first;
        return vector;
    }

    private async Task<ChunkEmbeddingReport> EmbedAsync(
        string connection,
        int batchSize = 32,
        bool retryOnFailure = false,
        CancellationToken? cancellationToken = null)
    {
        await using var dbContext = database.CreateDbContext(connection, retryOnFailure);
        var options = Options.Create(new EmbeddingOptions { Endpoint = "http://ollama.test:11434", Model = Model, BatchSize = batchSize });
        var embedder = new ChunkEmbedder(_generator, new ChunkEmbeddingRepository(dbContext), options, NullLogger<ChunkEmbedder>.Instance);
        return await embedder.RunAsync(cancellationToken ?? Ct);
    }

    private async Task AssertNothingStoredAsync(string connection)
    {
        await using var dbContext = database.CreateDbContext(connection);
        Assert.Equal(0, await dbContext.DocumentChunks.CountAsync(c => c.Embedding != null, Ct));
    }

    private async Task SaveDocumentAsync(string connection, string docId, char hashCharacter, string text)
    {
        await using var dbContext = database.CreateDbContext(connection);
        var document = new TenderDocument(docId, "100001-2026", "swz", "a.pdf", new string(hashCharacter, 64), null);
        await new DocumentRepository(dbContext, new FakeTimeProvider(Now)).SaveAsync(document, [new DocumentChunk(0, 1, text)], Ct);
    }

    private async Task<List<long>> SeedAsync(string connection, params (string DocId, int Chunks)[] documents)
    {
        await using var dbContext = database.CreateDbContext(connection);
        var entities = documents.Select(document =>
        {
            var entity = DocumentEntity.Create(new TenderDocument(document.DocId, "100001-2026", "swz", "a.pdf", new string('a', 64), null), Now);
            entity.Chunks.AddRange(Enumerable.Range(0, document.Chunks).Select(i => new DocumentChunkEntity
            {
                Position = i,
                PageNumber = i + 1,
                Text = $"{document.DocId} fragment {i}",
            }));
            return entity;
        }).ToList();

        dbContext.Documents.AddRange(entities);
        await dbContext.SaveChangesAsync(Ct);
        return [.. entities.SelectMany(entity => entity.Chunks).Select(chunk => chunk.Id).Order()];
    }

    private async Task SetEmbeddingAsync(string connection, long id, float[] vector, string model)
    {
        await using var dbContext = database.CreateDbContext(connection);
        await new ChunkEmbeddingRepository(dbContext).SaveAsync(model, [new ChunkEmbedding(id, vector)], Ct);
    }

    private async Task<List<float[]>> ReadVectorsAsync(string connection)
    {
        await using var dbContext = database.CreateDbContext(connection);
        var chunks = await dbContext.DocumentChunks.AsNoTracking().OrderBy(c => c.Id).ToListAsync(Ct);
        return [.. chunks.Select(chunk => chunk.Embedding!.ToArray())];
    }
}
