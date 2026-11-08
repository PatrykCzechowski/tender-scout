using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Documents;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Documents;

public sealed class DocumentImporterTests : IDisposable
{
    private const string ValidHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly DocumentTestFolder _folder = new();
    private readonly FakeDocumentRepository _repository = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_ValidRow_StoresDocumentFromConfiguredFolder()
    {
        var content = SyntheticPdf.WithText("Serwery");
        _folder.Add("swz-001", "100001-2026/SWZ.pdf", content);

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Imported);
        var (document, chunks) = _repository.Saved["swz-001"];
        Assert.Equal("100001-2026", document.PublicationNumber);
        Assert.Equal("swz", document.Kind);
        Assert.Equal("100001-2026/SWZ.pdf", document.Path);
        Assert.Equal(DocumentTestFolder.Hash(content), document.Sha256);
        Assert.Null(document.SkipReason);
        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task Run_MissingManifest_FailsWithoutStoring()
    {
        var options = _folder.Options();
        File.Delete(options.Value.ManifestPath);

        await Assert.ThrowsAsync<DocumentImportException>(() => RunAsync(options));

        Assert.Empty(_repository.Saved);
    }

    [Fact]
    public async Task Run_RowsOfOtherKinds_AreIgnoredWithoutError()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Serwery"));
        _folder.AddRow("odp-001", "not-there.pdf", ValidHash, kind: "odpowiedzi");
        _folder.AddRow("zm-001", "not-there-either.pdf", ValidHash, kind: "zmiana_swz");

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Imported);
        Assert.Equal(2, report.Ignored);
        Assert.Equal(["swz-001"], _repository.Saved.Keys);
    }

    [Theory]
    [InlineData("subfolder/file.pdf")]
    [InlineData("załącznik_śląskie.pdf")]
    [InlineData("SWZ cz 1.pdf")]
    [InlineData("SWZ..ostateczny.pdf")]
    public async Task Run_UnusualButValidPath_IsImported(string path)
    {
        _folder.Add("swz-001", path, SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Imported);
    }

    [Theory]
    [InlineData("../secret.pdf")]
    [InlineData("sub/../../outside.pdf")]
    [InlineData("/etc/passwd")]
    public void ResolvePath_OutsideDocumentsFolder_IsRejected(string path)
    {
        var row = new ManifestRow(1, "swz-001", "100001-2026", "swz", path, ValidHash);

        var exception = Assert.Throws<DocumentImportException>(() => DocumentImporter.ResolvePath("/tmp/test-docs", row));

        Assert.Contains("swz-001", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePath_SiblingFolderWithTheSamePrefix_IsRejected()
    {
        var row = new ManifestRow(1, "swz-001", "100001-2026", "swz", "../test-docs-other/a.pdf", ValidHash);

        Assert.Throws<DocumentImportException>(() => DocumentImporter.ResolvePath("/tmp/test-docs", row));
    }

    [Fact]
    public void ResolvePath_RelativePath_StaysInsideTheFolder()
    {
        var row = new ManifestRow(1, "swz-001", "100001-2026", "swz", "subfolder/file.pdf", ValidHash);

        var resolved = DocumentImporter.ResolvePath("/tmp/test-docs/", row);

        Assert.Equal(Path.GetFullPath("/tmp/test-docs/subfolder/file.pdf"), resolved);
    }

    [Fact]
    public async Task Run_MissingFile_RejectsRowAndContinues()
    {
        _folder.AddRow("swz-001", "missing/SWZ.pdf", ValidHash);
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Imported);
        var problem = Assert.Single(report.Rejected);
        Assert.Contains("swz-001", problem, StringComparison.Ordinal);
        Assert.Contains("missing/SWZ.pdf", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_UppercaseHashInManifest_IsAccepted()
    {
        var content = SyntheticPdf.WithText("Serwery");
        _folder.Add("swz-001", "a.pdf", content, sha256: DocumentTestFolder.Hash(content).ToUpperInvariant());

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(DocumentTestFolder.Hash(content), _repository.Saved["swz-001"].Document.Sha256);
    }

    [Fact]
    public async Task Run_HashMismatch_NamesDocumentAndBothHashes()
    {
        var content = SyntheticPdf.WithText("Serwery");
        _folder.Add("swz-001", "a.pdf", content, sha256: ValidHash);

        var report = await RunAsync();

        var problem = Assert.Single(report.Rejected);
        Assert.Contains("swz-001", problem, StringComparison.Ordinal);
        Assert.Contains(ValidHash, problem, StringComparison.Ordinal);
        Assert.Contains(DocumentTestFolder.Hash(content), problem, StringComparison.Ordinal);
        Assert.Empty(_repository.Saved);
    }

    [Fact]
    public async Task Run_HashMismatchInTheMiddle_ImportsTheOtherRows()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Pierwszy"));
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Drugi"), sha256: ValidHash);
        _folder.Add("swz-003", "c.pdf", SyntheticPdf.WithText("Trzeci"));

        var report = await RunAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(2, report.Imported);
        Assert.Single(report.Rejected);
        Assert.Equal(["swz-001", "swz-003"], _repository.Saved.Keys);
    }

    [Fact]
    public async Task Run_MalformedHash_RejectsRow()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Serwery"), sha256: "abc123");

        var report = await RunAsync();

        Assert.Contains("swz-001", Assert.Single(report.Rejected), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_DocumentListedTwice_RejectsTheSecondRow()
    {
        var content = SyntheticPdf.WithText("Serwery");
        _folder.Add("swz-001", "a.pdf", content);
        _folder.Add("swz-001", "b.pdf", content);

        var report = await RunAsync();

        Assert.Equal(1, report.Imported);
        Assert.Contains("more than once", Assert.Single(report.Rejected), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_PdfWithoutText_IsSkippedWithReason()
    {
        _folder.Add("swz-001", "scan.pdf", SyntheticPdf.Create([], []));
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Imported);
        var (document, chunks) = _repository.Saved["swz-001"];
        Assert.Equal(DocumentSkipReason.NoTextLayer, document.SkipReason);
        Assert.Empty(chunks);
    }

    [Theory]
    [InlineData("a.docx", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 })]
    [InlineData("a.doc", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1 })]
    public async Task Run_WordFile_IsSkippedWithItsOwnReason(string path, byte[] content)
    {
        _folder.Add("swz-001", path, content);
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(DocumentSkipReason.UnsupportedFormat, _repository.Saved["swz-001"].Document.SkipReason);
    }

    [Theory]
    [InlineData("this is not a pdf at all")]
    [InlineData("%PDF-1.7 and then the file breaks off")]
    public async Task Run_CorruptedPdf_RejectsRowAndContinues(string content)
    {
        _folder.Add("swz-001", "broken.pdf", Encoding.ASCII.GetBytes(content));
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.False(report.Succeeded);
        Assert.Contains("swz-001", Assert.Single(report.Rejected), StringComparison.Ordinal);
        Assert.Equal(["swz-002"], _repository.Saved.Keys);
    }

    [Fact]
    public async Task Run_RepositoryFailure_RejectsRowAndContinues()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Pierwszy"));
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Drugi"));
        _repository.FailOn = "swz-001";

        var report = await RunAsync();

        Assert.Contains("swz-001", Assert.Single(report.Rejected), StringComparison.Ordinal);
        Assert.Equal(1, report.Imported);
    }

    [Fact]
    public async Task Run_Cancelled_StopsInsteadOfRejectingRows()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Serwery"));
        var importer = new DocumentImporter(_repository, _folder.Options(), NullLogger<DocumentImporter>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => importer.RunAsync(new CancellationToken(canceled: true)));
    }

    public void Dispose() => _folder.Dispose();

    private Task<DocumentImportReport> RunAsync(IOptions<DocumentImportOptions>? options = null) =>
        new DocumentImporter(_repository, options ?? _folder.Options(), NullLogger<DocumentImporter>.Instance).RunAsync(Ct);

    private sealed class FakeDocumentRepository : IDocumentRepository
    {
        public string? FailOn { get; set; }

        public Dictionary<string, (TenderDocument Document, IReadOnlyList<DocumentChunk> Chunks)> Saved { get; } = [];

        public Task<string?> FindHashAsync(string docId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved.TryGetValue(docId, out var saved) ? saved.Document.Sha256 : null);

        public Task<DocumentSaveResult> SaveAsync(
            TenderDocument document,
            IReadOnlyList<DocumentChunk> chunks,
            CancellationToken cancellationToken = default)
        {
            if (document.DocId == FailOn)
            {
                throw new InvalidOperationException("Simulated storage failure.");
            }

            var result = Saved.ContainsKey(document.DocId) ? DocumentSaveResult.Updated : DocumentSaveResult.Inserted;
            Saved[document.DocId] = (document, chunks);
            return Task.FromResult(result);
        }
    }
}
