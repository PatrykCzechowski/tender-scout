using TenderScout.Infrastructure.Documents;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Documents;

public sealed class PdfTextExtractorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tender-scout-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void ExtractAndSplit_MultiPagePdf_KeepsOneBasedPageNumbers()
    {
        var chunks = ExtractAndSplit(
            ["Rozdzial I", "Tekst ze strony pierwszej."],
            ["Rozdzial II", "Tekst ze strony drugiej."],
            ["Rozdzial III", "Tekst ze strony trzeciej."]);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(1, Assert.Single(chunks, c => c.Text.Contains("pierwszej", StringComparison.Ordinal)).PageNumber);
        Assert.Equal(2, Assert.Single(chunks, c => c.Text.Contains("drugiej", StringComparison.Ordinal)).PageNumber);
        Assert.Equal(3, Assert.Single(chunks, c => c.Text.Contains("trzeciej", StringComparison.Ordinal)).PageNumber);
    }

    [Fact]
    public void ExtractAndSplit_SectionAcrossPages_CitesThePageItStartsOn()
    {
        var chunk = Assert.Single(ExtractAndSplit(
            ["Rozdzial I", "Poczatek na stronie pierwszej."],
            ["Ciag dalszy na stronie drugiej."]));

        Assert.Equal(1, chunk.PageNumber);
        Assert.Contains("drugiej", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractAndSplit_EmptyFirstPage_KeepsTruePageNumbers()
    {
        var chunks = ExtractAndSplit(
            [],
            ["Rozdzial I", "Tekst ze strony drugiej."],
            ["Rozdzial II", "Tekst ze strony trzeciej."]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(2, chunks[0].PageNumber);
        Assert.Equal(3, chunks[1].PageNumber);
    }

    public void Dispose() => File.Delete(_path);

    private List<Domain.Documents.DocumentChunk> ExtractAndSplit(params string[][] pages)
    {
        File.WriteAllBytes(_path, SyntheticPdf.Create(pages));
        return [.. DocumentChunker.Split(PdfTextExtractor.Extract(_path), 1000, 100)];
    }
}
