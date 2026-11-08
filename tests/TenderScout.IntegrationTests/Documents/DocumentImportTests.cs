using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Documents;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Documents;

public sealed class DocumentImportTests(PostgresFixture database) : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly DocumentTestFolder _folder = new();

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
        _folder.Add(docId, "a.pdf", SyntheticPdf.WithText("Serwery"));
        await RunAsync();
        var id = (await LoadAsync(docId)).Id;

        await using var dbContext = database.CreateDbContext();
        await dbContext.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(Ct);

        Assert.Equal(0, await dbContext.DocumentChunks.CountAsync(c => c.DocumentId == id, Ct));
    }

    [Fact]
    public async Task Run_ValidManifest_StoresDocumentsAndChunksInReadingOrder()
    {
        var first = UniqueId();
        var second = UniqueId();
        var content = SyntheticPdf.Create(
            ["Rozdzial I", "Tekst ze strony pierwszej dla importu dokumentu."],
            ["Rozdzial II", "Tekst ze strony drugiej dla importu dokumentu."]);
        _folder.Add(first, "a b/pierwszy.pdf", content);
        _folder.Add(second, "drugi.pdf", SyntheticPdf.WithText(second));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Imported);
        var document = await LoadAsync(first);
        Assert.Equal("100001-2026", document.PublicationNumber);
        Assert.Equal("swz", document.Kind);
        Assert.Equal("a b/pierwszy.pdf", document.Path);
        Assert.Equal(DocumentTestFolder.Hash(content), document.Sha256);
        Assert.Null(document.SkipReason);
        Assert.Equal(Now, document.CreatedAt);
        Assert.Equal([0, 1], document.Chunks.Select(c => c.Position));
        Assert.Equal([1, 2], document.Chunks.Select(c => c.PageNumber));
        Assert.Contains("pierwszej", document.Chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_PdfWithoutText_IsRecordedAsSkippedAndTheRunSucceeds()
    {
        var before = UniqueId();
        var scan = UniqueId();
        var after = UniqueId();
        _folder.Add(before, "a.pdf", SyntheticPdf.WithText(before));
        _folder.Add(scan, "scan.pdf", SyntheticPdf.Create([], []));
        _folder.Add(after, "c.pdf", SyntheticPdf.WithText(after));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Imported);
        Assert.Equal(1, report.Skipped);
        var skipped = await LoadAsync(scan);
        Assert.Equal(DocumentSkipReason.NoTextLayer, skipped.SkipReason);
        Assert.Empty(skipped.Chunks);
    }

    [Fact]
    public async Task Run_WordFile_IsRecordedWithItsOwnReason()
    {
        var word = UniqueId();
        var pdf = UniqueId();
        _folder.Add(word, "a.docx", [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]);
        _folder.Add(pdf, "b.pdf", SyntheticPdf.WithText(pdf));

        var report = await RunAsync();

        Assert.Equal(1, report.Imported);
        var skipped = await LoadAsync(word);
        Assert.Equal(DocumentSkipReason.UnsupportedFormat, skipped.SkipReason);
        Assert.Empty(skipped.Chunks);
    }

    [Fact]
    public async Task Run_HashMismatch_StoresNothingForThatRowAndFailsTheRun()
    {
        var wrong = UniqueId();
        var valid = UniqueId();
        _folder.Add(wrong, "a.pdf", SyntheticPdf.WithText(wrong), sha256: new string('a', 64));
        _folder.Add(valid, "b.pdf", SyntheticPdf.WithText(valid));

        var report = await RunAsync();

        Assert.False(report.Succeeded);
        Assert.Contains(wrong, Assert.Single(report.Rejected), StringComparison.Ordinal);
        await using var dbContext = database.CreateDbContext();
        Assert.False(await dbContext.Documents.AnyAsync(d => d.DocId == wrong, Ct));
        Assert.NotEmpty((await LoadAsync(valid)).Chunks);
    }

    [Fact]
    public async Task Run_FileChangedButManifestNot_LeavesStoredDataUntouched()
    {
        var docId = UniqueId();
        _folder.Add(docId, "a.pdf", SyntheticPdf.WithText("Pierwsza wersja"));
        await RunAsync();
        var before = await LoadAsync(docId);

        _folder.WriteFile("a.pdf", SyntheticPdf.WithText("Podmieniona wersja"));
        var report = await RunAsync();

        Assert.False(report.Succeeded);
        var after = await LoadAsync(docId);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(before.Chunks.Select(c => (c.Id, c.Text)), after.Chunks.Select(c => (c.Id, c.Text)));
    }

    [Fact]
    public async Task Run_SameManifestTwice_ChangesNothingAndKeepsChunkIds()
    {
        var imported = UniqueId();
        var scan = UniqueId();
        _folder.Add(imported, "a.pdf", SyntheticPdf.WithText(imported));
        _folder.Add(scan, "scan.pdf", SyntheticPdf.Create([], []));
        await RunAsync();
        var before = await LoadAsync(imported);

        _time.Advance(TimeSpan.FromHours(1));
        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Unchanged);
        Assert.Equal(0, report.Imported + report.Updated + report.Skipped);
        var after = await LoadAsync(imported);
        Assert.Equal(before.Chunks.Select(c => c.Id), after.Chunks.Select(c => c.Id));
        Assert.Equal(Now, after.UpdatedAt);
        await using var dbContext = database.CreateDbContext();
        Assert.Equal(1, await dbContext.Documents.CountAsync(d => d.DocId == scan, Ct));
    }

    [Fact]
    public async Task Run_NewVersionOfDocument_ReplacesItsChunks()
    {
        var docId = UniqueId();
        _folder.Add(docId, "a.pdf", SyntheticPdf.WithText("Pierwsza wersja"));
        await RunAsync();
        var before = await LoadAsync(docId);

        _time.Advance(TimeSpan.FromHours(1));
        var newContent = SyntheticPdf.Create(
            ["Rozdzial I", "Tresc nowej wersji dokumentu na pierwszej stronie."],
            ["Rozdzial II", "Tresc nowej wersji dokumentu na drugiej stronie."]);
        _folder.WriteFile("a.pdf", newContent);
        _folder.SetHash(docId, newContent);
        var report = await RunAsync();

        Assert.Equal(1, report.Updated);
        var after = await LoadAsync(docId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(DocumentTestFolder.Hash(newContent), after.Sha256);
        Assert.Equal(Now, after.CreatedAt);
        Assert.Equal(Now.AddHours(1), after.UpdatedAt);
        Assert.Equal([0, 1], after.Chunks.Select(c => c.Position));
        Assert.All(after.Chunks, c => Assert.Contains("nowej wersji", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_SkippedDocumentGainsText_BecomesImported()
    {
        var docId = UniqueId();
        _folder.Add(docId, "a.pdf", SyntheticPdf.Create([]));
        await RunAsync();

        var newContent = SyntheticPdf.WithText(docId);
        _folder.WriteFile("a.pdf", newContent);
        _folder.SetHash(docId, newContent);
        await RunAsync();

        var after = await LoadAsync(docId);
        Assert.Null(after.SkipReason);
        Assert.NotEmpty(after.Chunks);
    }

    [Fact]
    public async Task Run_FailureWhileStoringChunks_LeavesNoPartialDocumentAndRerunCompletes()
    {
        var first = UniqueId();
        var failing = UniqueId();
        var last = UniqueId();
        _folder.Add(first, "a.pdf", SyntheticPdf.WithText(first));
        _folder.Add(failing, "b.pdf", SyntheticPdf.WithText(failing));
        _folder.Add(last, "c.pdf", SyntheticPdf.WithText(last));

        var report = await RunAsync(interceptor: new FailChunkInsert(failing));

        Assert.False(report.Succeeded);
        Assert.Contains(failing, Assert.Single(report.Rejected), StringComparison.Ordinal);
        await using (var dbContext = database.CreateDbContext())
        {
            Assert.False(await dbContext.Documents.AnyAsync(d => d.DocId == failing, Ct));
        }

        var rerun = await RunAsync();

        Assert.True(rerun.Succeeded);
        Assert.Equal(1, rerun.Imported);
        Assert.Equal(2, rerun.Unchanged);
        Assert.NotEmpty((await LoadAsync(failing)).Chunks);
    }

    [Fact]
    public async Task Run_FailureWhileReplacingChunks_KeepsThePreviousVersion()
    {
        var docId = UniqueId();
        _folder.Add(docId, "a.pdf", SyntheticPdf.WithText("Pierwsza wersja"));
        await RunAsync();
        var before = await LoadAsync(docId);

        var newContent = SyntheticPdf.WithText("Znacznik drugiej wersji");
        _folder.WriteFile("a.pdf", newContent);
        _folder.SetHash(docId, newContent);
        var report = await RunAsync(interceptor: new FailChunkInsert("Znacznik drugiej wersji"));

        Assert.False(report.Succeeded);
        var after = await LoadAsync(docId);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(before.Chunks.Select(c => (c.Id, c.Text)), after.Chunks.Select(c => (c.Id, c.Text)));
    }

    [Fact]
    public async Task Run_WithRetryOnFailureEnabled_StoresAndReplacesDocuments()
    {
        var docId = UniqueId();
        _folder.Add(docId, "a.pdf", SyntheticPdf.WithText("Pierwsza wersja"));
        await RunAsync(retryOnFailure: true);

        var newContent = SyntheticPdf.WithText("Druga wersja");
        _folder.WriteFile("a.pdf", newContent);
        _folder.SetHash(docId, newContent);
        var report = await RunAsync(retryOnFailure: true);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Updated);
    }

    [Fact]
    public async Task Run_DocumentListedTwice_StoresOneSetOfChunks()
    {
        var docId = UniqueId();
        var content = SyntheticPdf.WithText(docId);
        _folder.Add(docId, "a.pdf", content);
        _folder.Add(docId, "b.pdf", content);

        var report = await RunAsync();

        Assert.Equal(1, report.Imported);
        Assert.Single(report.Rejected);
        await using var dbContext = database.CreateDbContext();
        Assert.Equal(1, await dbContext.Documents.CountAsync(d => d.DocId == docId, Ct));
    }

    public void Dispose() => _folder.Dispose();

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

    /// <summary>Each run uses its own context, like separate runs of the command.</summary>
    private async Task<DocumentImportReport> RunAsync(FailChunkInsert? interceptor = null, bool retryOnFailure = false)
    {
        var builder = new DbContextOptionsBuilder<TenderScoutDbContext>().UseNpgsql(database.ConnectionString, npgsql =>
        {
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
        IOptions<DocumentImportOptions> options = _folder.Options();
        var importer = new DocumentImporter(new DocumentRepository(dbContext, _time), options, NullLogger<DocumentImporter>.Instance);
        return await importer.RunAsync(Ct);
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
