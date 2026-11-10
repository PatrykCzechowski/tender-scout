using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Documents;

public sealed class DocumentRepositoryTests(PostgresFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_CreateDocumentTablesNextToNotices()
    {
        await using var dbContext = database.CreateDbContext();

        var tables = await dbContext.Database
            .SqlQuery<string>($"SELECT table_name::text AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync(Ct);

        Assert.Contains("documents", tables);
        Assert.Contains("document_chunks", tables);
        Assert.Contains("notices", tables);
    }

    [Fact]
    public async Task DocumentsTable_RejectsDuplicateDocId()
    {
        var docId = UniqueId();
        await using var dbContext = database.CreateDbContext();
        dbContext.Documents.Add(DocumentEntity.Create(CreateDocument(docId), Now));
        dbContext.Documents.Add(DocumentEntity.Create(CreateDocument(docId), Now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(Ct));

        AssertUniqueViolation(exception, "ux_documents_doc_id");
    }

    [Fact]
    public async Task ChunksTable_RejectsDuplicatePosition()
    {
        await using var dbContext = database.CreateDbContext();
        var document = DocumentEntity.Create(CreateDocument(UniqueId()), Now);
        document.Chunks.Add(new DocumentChunkEntity { Position = 0, PageNumber = 1, Text = "pierwszy" });
        document.Chunks.Add(new DocumentChunkEntity { Position = 0, PageNumber = 1, Text = "drugi" });
        dbContext.Documents.Add(document);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(Ct));

        AssertUniqueViolation(exception, "ux_document_chunks_document_id_position");
    }

    [Fact]
    public async Task DeletingDocument_RemovesItsChunks()
    {
        var docId = UniqueId();
        await SaveAsync(CreateDocument(docId), Chunks("Tekst"));
        var id = (await LoadAsync(docId)).Id;

        await using var dbContext = database.CreateDbContext();
        await dbContext.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(Ct);

        Assert.Equal(0, await dbContext.DocumentChunks.CountAsync(c => c.DocumentId == id, Ct));
    }

    [Fact]
    public async Task Save_NewDocument_StoresFieldsAndChunks()
    {
        var document = CreateDocument(UniqueId());

        var result = await SaveAsync(document, Chunks("Pierwszy", "Drugi"));

        Assert.Equal(DocumentSaveResult.Inserted, result);
        var row = await LoadAsync(document.DocId);
        Assert.Equal(document.PublicationNumber, row.PublicationNumber);
        Assert.Equal(document.Kind, row.Kind);
        Assert.Equal(document.Path, row.Path);
        Assert.Equal(document.Sha256, row.Sha256);
        Assert.Null(row.SkipReason);
        Assert.Equal(Now, row.CreatedAt);
        Assert.Equal([0, 1], row.Chunks.Select(c => c.Position));
        Assert.Equal(["Pierwszy", "Drugi"], row.Chunks.Select(c => c.Text));
    }

    [Fact]
    public async Task Save_NewVersion_ReplacesChunksAndKeepsTheRow()
    {
        var document = CreateDocument(UniqueId());
        await SaveAsync(document, Chunks("Stary"));
        var before = await LoadAsync(document.DocId);

        _time.Advance(TimeSpan.FromHours(1));
        var result = await SaveAsync(document with { Sha256 = new string('b', 64) }, Chunks("Nowy 1", "Nowy 2"));

        Assert.Equal(DocumentSaveResult.Updated, result);
        var after = await LoadAsync(document.DocId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(new string('b', 64), after.Sha256);
        Assert.Equal(Now, after.CreatedAt);
        Assert.Equal(Now.AddHours(1), after.UpdatedAt);
        Assert.Equal(["Nowy 1", "Nowy 2"], after.Chunks.Select(c => c.Text));
    }

    [Fact]
    public async Task Save_SkippedDocumentGainsText_ClearsTheReason()
    {
        var document = CreateDocument(UniqueId());
        await SaveAsync(document with { SkipReason = DocumentSkipReason.NoTextLayer }, []);

        await SaveAsync(document with { Sha256 = new string('b', 64) }, Chunks("Tekst"));

        var after = await LoadAsync(document.DocId);
        Assert.Null(after.SkipReason);
        Assert.NotEmpty(after.Chunks);
    }

    [Fact]
    public async Task FindHash_ReturnsTheStoredHashOrNull()
    {
        var document = CreateDocument(UniqueId());
        await SaveAsync(document, Chunks("Tekst"));

        await using var dbContext = database.CreateDbContext();
        var repository = new DocumentRepository(dbContext, _time);

        Assert.Equal(document.Sha256, await repository.FindHashAsync(document.DocId, Ct));
        Assert.Null(await repository.FindHashAsync(UniqueId(), Ct));
    }

    [Fact]
    public async Task Save_FailureWhileStoringChunks_LeavesNoPartialDocumentAndCanBeRepeated()
    {
        var document = CreateDocument(UniqueId());

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(document, Chunks("Znacznik"), new FailChunkInsert("Znacznik")));

        await using (var dbContext = database.CreateDbContext())
        {
            Assert.False(await dbContext.Documents.AnyAsync(d => d.DocId == document.DocId, Ct));
        }

        await SaveAsync(document, Chunks("Znacznik"));
        Assert.Single((await LoadAsync(document.DocId)).Chunks);
    }

    [Fact]
    public async Task Save_FailureWhileReplacingChunks_KeepsThePreviousVersion()
    {
        var document = CreateDocument(UniqueId());
        await SaveAsync(document, Chunks("Pierwsza wersja"));
        var before = await LoadAsync(document.DocId);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(
            document with { Sha256 = new string('b', 64) },
            Chunks("Znacznik drugiej wersji"),
            new FailChunkInsert("Znacznik drugiej wersji")));

        var after = await LoadAsync(document.DocId);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(before.Chunks.Select(c => (c.Id, c.Text)), after.Chunks.Select(c => (c.Id, c.Text)));
    }

    [Fact]
    public async Task Save_WithRetryOnFailureEnabled_StoresAndReplaces()
    {
        var document = CreateDocument(UniqueId());
        await SaveAsync(document, Chunks("Pierwsza wersja"), retryOnFailure: true);

        var result = await SaveAsync(document with { Sha256 = new string('b', 64) }, Chunks("Druga wersja"), retryOnFailure: true);

        Assert.Equal(DocumentSaveResult.Updated, result);
    }

    private static void AssertUniqueViolation(DbUpdateException exception, string constraint)
    {
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }

    /// <summary>Tests share one database, so each uses its own document ids.</summary>
    private static string UniqueId() => $"doc-{Guid.NewGuid():N}";

    private static TenderDocument CreateDocument(string docId) =>
        new(docId, "100001-2026", "swz", "a.pdf", new string('a', 64), null);

    private static DocumentChunk[] Chunks(params string[] texts) =>
        [.. texts.Select((text, position) => new DocumentChunk(position, position + 1, text))];

    /// <summary>Each save uses its own context, like separate deliveries of a message.</summary>
    private async Task<DocumentSaveResult> SaveAsync(
        TenderDocument document,
        DocumentChunk[] chunks,
        FailChunkInsert? interceptor = null,
        bool retryOnFailure = false)
    {
        var builder = new DbContextOptionsBuilder<TenderScoutDbContext>().UseNpgsql(database.ConnectionString, npgsql =>
        {
            npgsql.UseVector();
            if (retryOnFailure)
            {
                npgsql.EnableRetryOnFailure();
            }
        });

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        await using var dbContext = new TenderScoutDbContext(builder.Options);
        return await new DocumentRepository(dbContext, _time).SaveAsync(document, chunks, Ct);
    }

    private async Task<DocumentEntity> LoadAsync(string docId)
    {
        await using var dbContext = database.CreateDbContext();
        var document = await dbContext.Documents.AsNoTracking().Include(d => d.Chunks).SingleAsync(d => d.DocId == docId, Ct);
        document.Chunks.Sort((a, b) => a.Position.CompareTo(b.Position));
        return document;
    }

    /// <summary>Fails inside the transaction of one document, after its row has been written.</summary>
    private sealed class FailChunkInsert(string marker) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var insertsMarkedChunk = command.CommandText.Contains("document_chunks", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is string text && text.Contains(marker, StringComparison.Ordinal));

            return insertsMarkedChunk
                ? throw new InvalidOperationException("Simulated failure while storing chunks.")
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
