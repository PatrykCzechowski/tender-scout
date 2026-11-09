using System.Text;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenderScout.Contracts.Documents;
using TenderScout.Infrastructure.Documents;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Documents;

public sealed class DocumentImporterTests : IAsyncLifetime
{
    private const string ValidHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly DocumentTestFolder _folder = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddMassTransitTestHarness().BuildServiceProvider();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    public async ValueTask InitializeAsync() => await Harness.Start();

    [Fact]
    public async Task Run_ValidRow_PublishesManifestFieldsAndFullPath()
    {
        _folder.Add("swz-001", "100001-2026/SWZ.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Published);
        var message = Assert.Single(Published());
        var fullPath = Path.GetFullPath(Path.Combine(_folder.DocumentsFolder, "100001-2026/SWZ.pdf"));
        Assert.Equal(new DocumentUploaded("swz-001", "100001-2026", "swz", "100001-2026/SWZ.pdf", fullPath), message);
    }

    [Fact]
    public async Task Run_MissingManifest_FailsWithoutPublishing()
    {
        var options = _folder.Options();
        File.Delete(options.Value.ManifestPath);

        await Assert.ThrowsAsync<DocumentImportException>(() => RunAsync(options));

        Assert.Empty(Published());
    }

    [Fact]
    public async Task Run_ManifestWithoutRequiredColumn_FailsWithoutPublishing()
    {
        var options = _folder.Options();
        File.WriteAllText(options.Value.ManifestPath, "doc_id,path\nswz-001,a.pdf\n");

        await Assert.ThrowsAsync<DocumentImportException>(() => RunAsync(options));

        Assert.Empty(Published());
    }

    [Fact]
    public async Task Run_RowsOfOtherKinds_AreIgnoredWithoutError()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Serwery"));
        _folder.AddRow("odp-001", "not-there.pdf", ValidHash, kind: "odpowiedzi");
        _folder.AddRow("zm-001", "not-there-either.pdf", ValidHash, kind: "zmiana_swz");

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Published);
        Assert.Equal(2, report.Ignored);
        Assert.Equal(["swz-001"], Published().Select(m => m.DocId));
    }

    [Theory]
    [InlineData("subfolder/file.pdf")]
    [InlineData("załącznik_śląskie.pdf")]
    [InlineData("SWZ cz 1.pdf")]
    [InlineData("SWZ..ostateczny.pdf")]
    public async Task Run_UnusualButValidPath_IsPublished(string path)
    {
        _folder.Add("swz-001", path, SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(path, Assert.Single(Published()).Path);
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
    public async Task Run_PathOutsideTheFolder_RejectsRowAndContinues()
    {
        _folder.AddRow("swz-001", "../outside.pdf", ValidHash);
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.False(report.Succeeded);
        Assert.Contains("swz-001", Assert.Single(report.Rejected), StringComparison.Ordinal);
        Assert.Equal(["swz-002"], Published().Select(m => m.DocId));
    }

    [Fact]
    public async Task Run_MissingFile_RejectsRowAndContinues()
    {
        _folder.AddRow("swz-001", "missing/SWZ.pdf", ValidHash);
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await RunAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Published);
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
        Assert.Single(Published());
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
        Assert.Empty(Published());
    }

    [Fact]
    public async Task Run_HashMismatchInTheMiddle_PublishesTheOtherRows()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Pierwszy"));
        _folder.Add("swz-002", "b.pdf", SyntheticPdf.WithText("Drugi"), sha256: ValidHash);
        _folder.Add("swz-003", "c.pdf", SyntheticPdf.WithText("Trzeci"));

        var report = await RunAsync();

        Assert.False(report.Succeeded);
        Assert.Equal(2, report.Published);
        Assert.Single(report.Rejected);
        Assert.Equal(["swz-001", "swz-003"], Published().Select(m => m.DocId).Order());
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

        Assert.Equal(1, report.Published);
        Assert.Contains("more than once", Assert.Single(report.Rejected), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_FileTheConsumerWillSkip_IsStillPublished()
    {
        _folder.Add("swz-001", "a.docx", Encoding.ASCII.GetBytes("PK not a pdf"));

        var report = await RunAsync();

        Assert.True(report.Succeeded);
        Assert.Equal("a.docx", Assert.Single(Published()).Path);
    }

    [Fact]
    public async Task Run_SameManifestTwice_PublishesEveryRowAgain()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Serwery"));

        await RunAsync();
        await RunAsync();

        Assert.Equal(2, Published().Count);
    }

    [Fact]
    public async Task Run_Cancelled_StopsInsteadOfRejectingRows()
    {
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Serwery"));
        var importer = new DocumentImporter(Harness.Bus, _folder.Options(), NullLogger<DocumentImporter>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => importer.RunAsync(new CancellationToken(canceled: true)));
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _folder.Dispose();
    }

    private Task<DocumentImportReport> RunAsync(IOptions<DocumentImportOptions>? options = null) =>
        new DocumentImporter(Harness.Bus, options ?? _folder.Options(), NullLogger<DocumentImporter>.Instance).RunAsync(Ct);

    private List<DocumentUploaded> Published() =>
        [.. Harness.Published.Select<DocumentUploaded>(Ct).Select(m => m.Context.Message)];
}
