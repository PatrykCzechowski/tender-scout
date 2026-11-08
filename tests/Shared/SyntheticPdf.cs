using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace TenderScout.Tests.Shared;

/// <summary>Builds small PDFs with invented text, so no real tender document is needed in tests.</summary>
internal static class SyntheticPdf
{
    private const double LeftMargin = 50;
    private const double TopOfPage = 780;
    private const double LineHeight = 40;

    /// <param name="pages">Lines of each page; a page without lines has no text layer. Use ASCII: the font has no Polish letters.</param>
    public static byte[] Create(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = TopOfPage;
            foreach (var line in lines)
            {
                page.AddText(line, 11, new PdfPoint(LeftMargin, y), font);
                y -= LineHeight;
            }
        }

        return builder.Build();
    }

    /// <summary>A one-page PDF with enough text to count as having a text layer.</summary>
    public static byte[] WithText(string marker) =>
        Create([$"Rozdzial I. {marker}", $"Zamawiajacy opisuje przedmiot zamowienia dla dokumentu {marker} w tym akapicie."]);
}
