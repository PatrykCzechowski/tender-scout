using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace TenderScout.Infrastructure.Documents;

/// <summary>Reads the text of a PDF page by page, with a blank line between paragraphs.</summary>
internal static class PdfTextExtractor
{
    private static readonly ContentOrderTextExtractor.Options TextOptions = new()
    {
        ReplaceWhitespaceWithSpace = true,
        SeparateParagraphsWithDoubleNewline = true,
    };

    public static IReadOnlyList<PageText> Extract(string path)
    {
        using var document = PdfDocument.Open(path);
        return [.. document.GetPages().Select(page => new PageText(page.Number, ContentOrderTextExtractor.GetText(page, TextOptions)))];
    }
}
