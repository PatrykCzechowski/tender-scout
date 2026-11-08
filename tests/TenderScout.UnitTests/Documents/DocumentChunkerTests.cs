using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Documents;

namespace TenderScout.UnitTests.Documents;

public sealed class DocumentChunkerTests
{
    [Fact]
    public void Split_ShortSections_StartEachAtItsHeading()
    {
        var chunks = Split(
            """
            Rozdział I. Przedmiot zamówienia

            Przedmiotem zamówienia jest dostawa serwerów.

            § 2. Warunki udziału

            Wykonawca posiada doświadczenie.

            3. Termin wykonania

            Termin wykonania wynosi 30 dni.
            """,
            size: 1000,
            overlap: 100);

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith("Rozdział I.", chunks[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("§ 2.", chunks[1].Text, StringComparison.Ordinal);
        Assert.StartsWith("3. Termin", chunks[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_ListUnderHeading_StaysInOneChunk()
    {
        var chunk = Assert.Single(Split(
            """
            Rozdział V. Wymagane dokumenty

            1) dowód osobisty,
            2) odpis KRS,
            3) zaświadczenie ZUS
            """,
            size: 1000,
            overlap: 100));

        Assert.Contains("3) zaświadczenie ZUS", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_NumberedSentenceWrappedOntoNextLine_IsNotAHeading()
    {
        var chunk = Assert.Single(Split(
            """
            1. Oferent pozostaje zobowiązany swoją propozycją od pierwszego dnia po zakończeniu naboru
            aż do daty wskazanej w formularzu.
            2. Oferent po zakończeniu naboru traci prawo do wprowadzania jakichkolwiek poprawek
            oraz do rezygnacji.
            """,
            size: 1000,
            overlap: 100));

        Assert.Contains("2. Oferent", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_ConsecutiveHeadings_StayWithTheTextTheyIntroduce()
    {
        var chunk = Assert.Single(Split("Rozdział 9\n\nTERMIN ZWIĄZANIA OFERTĄ\n\nWykonawca jest związany ofertą.", size: 1000, overlap: 100));

        Assert.StartsWith("Rozdział 9", chunk.Text, StringComparison.Ordinal);
        Assert.EndsWith("ofertą.", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_HeadingWithoutPolishLetters_StillStartsAChunk()
    {
        var chunks = Split("Wstęp dokumentu.\n\nRozdzial II\n\nTreść rozdziału.", size: 1000, overlap: 100);

        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("Rozdzial II", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_PageFooterInCapitals_IsNotAHeading()
    {
        var chunk = Assert.Single(Split("Treść pierwszego akapitu.\nKRS: 0000000001, NIP 0000000002, REGON: 000000003\nDalsza treść.", size: 1000, overlap: 100));

        Assert.Contains("Dalsza treść.", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_LongSection_BreaksBetweenParagraphs()
    {
        var paragraphs = Paragraphs(5, 300);

        var chunks = Split(string.Join("\n\n", paragraphs), size: 700, overlap: 100);

        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 700));
        Assert.All(paragraphs, paragraph => Assert.Contains(chunks, chunk => chunk.Text.Contains(paragraph, StringComparison.Ordinal)));
        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void Split_ParagraphLongerThanLimit_KeepsEveryWordWithinTheLimit()
    {
        var words = Enumerable.Range(0, 200).Select(i => $"słowo{i}").ToList();
        var text = string.Join(' ', words.Select((word, i) => i % 10 == 9 ? word + "." : word));

        var chunks = Split(text, size: 500, overlap: 50);

        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 500));
        var all = string.Join(' ', chunks.Select(chunk => chunk.Text));
        Assert.All(words, word => Assert.Contains(word, all, StringComparison.Ordinal));
    }

    [Fact]
    public void Split_UnbrokenTokenLongerThanLimit_IsCutWithoutLosingText()
    {
        var token = new string('x', 600);

        var chunks = Split(token, size: 250, overlap: 40);

        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 250));
        Assert.Equal(600, chunks.Sum(chunk => chunk.Text.Length));
    }

    [Fact]
    public void Split_ConsecutiveChunks_ShareBoundedOverlap()
    {
        var chunks = Split(string.Join("\n\n", Paragraphs(9, 300)), size: 700, overlap: 100);

        Assert.True(chunks.Count >= 3);
        foreach (var (previous, next) in chunks.Zip(chunks.Skip(1)))
        {
            Assert.InRange(SharedLength(previous.Text, next.Text), 1, 100);
        }
    }

    [Fact]
    public void Split_ZeroOverlap_RepeatsNothing()
    {
        var paragraphs = Paragraphs(9, 300);

        var chunks = Split(string.Join("\n\n", paragraphs), size: 700, overlap: 0);

        var joined = string.Join("\n\n", chunks.Select(chunk => chunk.Text));
        Assert.Equal(string.Join("\n\n", paragraphs), joined);
    }

    [Fact]
    public void Split_Overlap_DoesNotCrossIntoTheNextSection()
    {
        var chunks = Split("Rozdział I\n\nTreść pierwszego rozdziału.\n\nRozdział II\n\nTreść drugiego rozdziału.", size: 1000, overlap: 100);

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain("pierwszego", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SameInputTwice_GivesTheSameChunks()
    {
        var text = "Rozdział I. Zażółć gęślą jaźń\n\n" + string.Join("\n\n", Paragraphs(8, 280));

        Assert.Equal(Split(text, size: 600, overlap: 80), Split(text, size: 600, overlap: 80));
    }

    [Fact]
    public void Split_Positions_RunWithoutGapsInReadingOrder()
    {
        var chunks = Split(string.Join("\n\n", Paragraphs(12, 300)), size: 700, overlap: 100);

        Assert.True(chunks.Count >= 4);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.Position));
    }

    [Fact]
    public void Split_SmallerSettings_GiveMoreAndSmallerChunks()
    {
        var text = string.Join("\n\n", Paragraphs(12, 250));

        var large = Split(text, size: 1000, overlap: 100);
        var small = Split(text, size: 300, overlap: 50);

        Assert.True(small.Count > large.Count);
        Assert.All(small, chunk => Assert.InRange(chunk.Text.Length, 1, 300));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public void Split_BlankText_GivesNoChunks(string text)
    {
        Assert.Empty(Split(text, size: 500, overlap: 50));
    }

    [Fact]
    public void Split_ChunkContinuingOnNextPage_KeepsItsStartingPage()
    {
        var pages = new[] { new PageText(1, "Rozdział I\n\nPoczątek rozdziału."), new PageText(2, "Ciąg dalszy rozdziału.") };

        var chunk = Assert.Single(DocumentChunker.Split(pages, 1000, 100));

        Assert.Equal(1, chunk.PageNumber);
        Assert.Contains("Ciąg dalszy", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_OverlapFromThePreviousPage_DoesNotChangeThePageNumber()
    {
        var pages = new[]
        {
            new PageText(1, string.Join("\n\n", Paragraphs(2, 300))),
            new PageText(2, string.Join("\n\n", Paragraphs(2, 300, firstIndex: 2))),
        };

        var chunks = DocumentChunker.Split(pages, 700, 100);

        Assert.Equal(1, chunks[0].PageNumber);
        Assert.Equal(2, chunks[1].PageNumber);
        Assert.True(SharedLength(chunks[0].Text, chunks[1].Text) > 0);
    }

    private static IReadOnlyList<DocumentChunk> Split(string text, int size, int overlap) =>
        DocumentChunker.Split([new PageText(1, text)], size, overlap);

    private static List<string> Paragraphs(int count, int length, int firstIndex = 0) =>
        [.. Enumerable.Range(firstIndex, count).Select(i => Paragraph(i, length))];

    private static string Paragraph(int index, int length)
    {
        var words = string.Join(' ', Enumerable.Range(0, length).Select(i => $"p{index}w{i}"));
        return words[..length].TrimEnd();
    }

    private static int SharedLength(string previous, string next)
    {
        for (var length = Math.Min(previous.Length, next.Length); length > 0; length--)
        {
            if (previous.EndsWith(next[..length], StringComparison.Ordinal))
            {
                return length;
            }
        }

        return 0;
    }
}
