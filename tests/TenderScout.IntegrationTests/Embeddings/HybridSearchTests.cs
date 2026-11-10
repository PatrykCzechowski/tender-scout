using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Embeddings;

/// <summary>Fixed vectors and invented texts; the query vector is passed in, so no generator is involved.</summary>
public sealed class HybridSearchTests(PostgresFixture database)
{
    private const string Model = "bge-m3";
    private const string MigrationBeforeSearchVector = "20261108172145_AddChunkEmbeddings";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_AddsAGeneratedSearchVectorWithAGinIndex()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);

        var column = await dbContext.Database
            .SqlQuery<string>($"""
                SELECT data_type || ' ' || is_generated AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'document_chunks' AND column_name = 'search_vector'
                """)
            .SingleAsync(Ct);
        var index = await dbContext.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_document_chunks_search_vector'")
            .SingleAsync(Ct);

        Assert.Equal("tsvector ALWAYS", column);
        Assert.Contains("USING gin (search_vector)", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_FillsTheSearchVectorOfExistingChunks()
    {
        var connection = await database.CreateIsolatedDatabaseAsync(MigrationBeforeSearchVector);
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
                INSERT INTO document_chunks (document_id, position, page_number, text, embedding, embedding_model)
                SELECT id, 0, 3, 'Wymagania dotyczące wadium', {new Pgvector.Vector(Vector(1, 0))}, 'bge-m3' FROM documents
                """,
                Ct);
        }

        await using var after = database.CreateDbContext(connection);
        await after.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);

        var chunk = await after.DocumentChunks.AsNoTracking().SingleAsync(Ct);
        Assert.Contains("'wadium'", chunk.SearchVector.ToString(), StringComparison.Ordinal);
        Assert.Equal("Wymagania dotyczące wadium", chunk.Text);
        Assert.Equal(Vector(1, 0), chunk.Embedding!.ToArray());
    }

    [Fact]
    public async Task NewVersionOfAChunk_IsFoundByItsNewText()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var document = new TenderDocument("doc-1", "100001-2026", "swz", "a.pdf", new string('a', 64), null);
        await using (var dbContext = database.CreateDbContext(connection))
        {
            var repository = new DocumentRepository(dbContext, new FakeTimeProvider(Now));
            await repository.SaveAsync(document, [new DocumentChunk(0, 1, "Stary tekst")], Ct);
            await repository.SaveAsync(document with { Sha256 = new string('b', 64) }, [new DocumentChunk(0, 1, "Nowy tekst o wadium")], Ct);
        }

        var results = await HybridAsync(connection, "wadium", Vector(0, 1));

        Assert.Equal("Nowy tekst o wadium", Assert.Single(results).Text);
    }

    [Fact]
    public async Task ExactTerm_IsFoundByFullTextWhereVectorsMissIt()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Pierwszy fragment", Vector(1, 0.1f)),
            new("doc-1", 2, "Drugi fragment", Vector(1, 0.2f)),
            new("doc-1", 3, "Trzeci fragment", Vector(1, 0.3f)),
            new("doc-2", 4, "Czwarty fragment", Vector(1, 0.4f)),
            new("doc-2", 5, "Piąty fragment", Vector(1, 0.5f)),
            new("doc-2", 9, "Kod CPV 72000000-5 usługi informatyczne", Vector(0, 1)));

        await using var dbContext = database.CreateDbContext(connection);
        var vectorOnly = await new ChunkEmbeddingRepository(dbContext).SearchAsync(Vector(1, 0), Model, 5, cancellationToken: Ct);
        var hybrid = await HybridAsync(connection, "CPV 72000000", Vector(1, 0), limit: 5);

        Assert.DoesNotContain(vectorOnly, r => r.Text.Contains("72000000", StringComparison.Ordinal));
        var found = Assert.Single(hybrid, r => r.Text.Contains("72000000", StringComparison.Ordinal));
        Assert.Equal(("doc-2", 9), (found.DocId, found.PageNumber));
    }

    [Fact]
    public async Task CodeInTheQuestion_OutranksAFrequentWordNextToIt()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Kody CPV: cpv dla części, cpv główny, cpv dodatkowy", null),
            new("doc-2", 7, "Dostawa komputerów 30233100", null));

        var results = await HybridAsync(connection, "CPV 30233100", Vector(1, 0));

        Assert.Equal(("doc-2", 7), (results[0].DocId, results[0].PageNumber));
    }

    [Fact]
    public async Task MeaningMatch_IsFoundByVectorsWithoutSharedWords()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Zabezpieczenie oferty w kwocie dziesięciu tysięcy", Vector(1, 0)),
            new("doc-1", 2, "Opis przedmiotu zamówienia", Vector(0, 1)));

        var results = await HybridAsync(connection, "Jaka jest wysokość wadium?", Vector(1, 0));

        Assert.Equal("Zabezpieczenie oferty w kwocie dziesięciu tysięcy", results[0].Text);
    }

    [Fact]
    public async Task ChunkFirstInBothRankings_ScoresTwiceTheTopOfOne()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Wysokość wadium", Vector(1, 0)),
            new("doc-1", 2, "Opis przedmiotu", Vector(0, 1)));

        var results = await HybridAsync(connection, "wadium", Vector(1, 0));

        Assert.Equal(2.0 / 61, results[0].Score, 10);
        Assert.Equal(1.0 / 62, results[1].Score, 10);
    }

    [Fact]
    public async Task FoundByBoth_RanksAboveTheTopOfEitherRanking()
    {
        // C is first by text only, A second in both rankings, B first by vectors only.
        var connection = await SeedAsync(
            new("doc-1", 1, "C: wadium", null),
            new("doc-1", 2, "A: wadium", Vector(0.9f, 0.1f)),
            new("doc-2", 3, "B: zupełnie inne słowa", Vector(1, 0)));

        var first = await HybridAsync(connection, "wadium", Vector(1, 0));
        var second = await HybridAsync(connection, "wadium", Vector(1, 0));

        Assert.Equal(["A: wadium", "C: wadium", "B: zupełnie inne słowa"], first.Select(r => r.Text));
        Assert.Equal(2.0 / 62, first[0].Score, 10);
        Assert.Equal(first[1].Score, first[2].Score, 10);
        Assert.Equal(first.Select(r => r.ChunkId), second.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task Diacritics_AreKept()
    {
        var connection = await SeedAsync(new Seed("doc-1", 1, "Wysokość wadium", null));

        Assert.Single(await HybridAsync(connection, "wysokość", Vector(1, 0)));
        Assert.Empty(await HybridAsync(connection, "wysokosc", Vector(1, 0)));
    }

    [Fact]
    public async Task QuestionWords_DoNotMakeAChunkACandidate()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Jaka jest pogoda", null),
            new("doc-1", 2, "Kwota wadium", null));

        var results = await HybridAsync(connection, "Jaka jest wysokość wadium?", Vector(1, 0));

        Assert.Equal("Kwota wadium", Assert.Single(results).Text);
    }

    [Theory]
    [InlineData("-wadium \"termin")]
    [InlineData("wadium | !termin & (")]
    public async Task OperatorsInTheQuestion_AreTreatedAsWords(string query)
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Kwota wadium", null),
            new("doc-1", 2, "Termin składania ofert", null));

        var results = await HybridAsync(connection, query, Vector(1, 0));

        Assert.Equal(2, results.Count);
    }

    [Theory]
    [InlineData("Jaka jest?")]
    [InlineData("?! ...")]
    public async Task NoUsableWords_StillReturnsVectorResults(string query)
    {
        var connection = await SeedAsync(new Seed("doc-1", 1, "Jaka jest kwota", Vector(1, 0)));

        Assert.Single(await HybridAsync(connection, query, Vector(1, 0)));
    }

    [Fact]
    public async Task VectorsOfAnotherModel_DoNotTakePart()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Same model", Vector(0, 1)),
            new("doc-1", 2, "Other model", Vector(1, 0), "other-model"));

        var results = await HybridAsync(connection, "pytanie", Vector(1, 0));

        Assert.Equal("Same model", Assert.Single(results).Text);
    }

    [Fact]
    public async Task Limit_CapsTheResults()
    {
        var connection = await SeedAsync(
            new("doc-1", 1, "Wadium pierwsze", Vector(1, 0)),
            new("doc-1", 2, "Wadium drugie", Vector(0.9f, 0.1f)),
            new("doc-1", 3, "Wadium trzecie", Vector(0.8f, 0.2f)));

        Assert.Equal(2, (await HybridAsync(connection, "wadium", Vector(1, 0), limit: 2)).Count);
    }

    private static float[] Vector(float first, float second)
    {
        var vector = new float[ChunkEmbedding.Dimensions];
        vector[0] = first;
        vector[1] = second;
        return vector;
    }

    private async Task<IReadOnlyList<ChunkSearchResult>> HybridAsync(string connection, string text, float[] vector, int limit = 10)
    {
        await using var dbContext = database.CreateDbContext(connection);
        return await new ChunkEmbeddingRepository(dbContext).HybridSearchAsync(text, vector, Model, limit, cancellationToken: Ct);
    }

    /// <summary>Stores chunks in the given order, so their ids follow it.</summary>
    private async Task<string> SeedAsync(params Seed[] seeds)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        foreach (var seed in seeds)
        {
            await using var dbContext = database.CreateDbContext(connection);
            var document = await dbContext.Documents.SingleOrDefaultAsync(d => d.DocId == seed.DocId, Ct);
            if (document is null)
            {
                document = DocumentEntity.Create(new TenderDocument(seed.DocId, "100001-2026", "swz", "a.pdf", new string('a', 64), null), Now);
                dbContext.Documents.Add(document);
                await dbContext.SaveChangesAsync(Ct);
            }

            dbContext.DocumentChunks.Add(new DocumentChunkEntity
            {
                DocumentId = document.Id,
                Position = await dbContext.DocumentChunks.CountAsync(c => c.DocumentId == document.Id, Ct),
                PageNumber = seed.Page,
                Text = seed.Text,
                Embedding = seed.Vector is null ? null : new Pgvector.Vector(seed.Vector),
                EmbeddingModel = seed.Vector is null ? null : seed.Model,
            });
            await dbContext.SaveChangesAsync(Ct);
        }

        return connection;
    }

    private sealed record Seed(string DocId, int Page, string Text, float[]? Vector, string Model = Model);
}
