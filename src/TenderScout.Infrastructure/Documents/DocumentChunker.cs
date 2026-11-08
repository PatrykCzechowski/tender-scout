using System.Text;
using System.Text.RegularExpressions;
using TenderScout.Domain.Documents;

namespace TenderScout.Infrastructure.Documents;

/// <summary>The text of one PDF page; <see cref="PageNumber"/> counts from 1.</summary>
internal sealed record PageText(int PageNumber, string Text);

/// <summary>
/// Splits a document into chunks: a heading starts a new chunk, and a longer section breaks between paragraphs.
/// Size and overlap are in characters, which needs no tokenizer and stays well within what the embedding model accepts.
/// Overlap stays inside a section, so a chunk never opens with the end of the previous topic.
/// </summary>
internal static partial class DocumentChunker
{
    private const string Separator = "\n\n";
    private const int MaxHeadingLength = 120;
    private const int MaxNumberedHeadingLength = 60;

    public static IReadOnlyList<DocumentChunk> Split(IEnumerable<PageText> pages, int chunkSize, int chunkOverlap)
    {
        // Pieces of an oversized paragraph leave room for the overlap, so it does not shrink on every piece.
        var pieceLimit = Math.Max(Math.Max(chunkSize - chunkOverlap - Separator.Length, chunkSize / 2), 1);
        var builder = new ChunkBuilder(chunkSize, chunkOverlap);

        foreach (var block in ReadBlocks(pages))
        {
            if (block.IsHeading)
            {
                builder.StartSection();
            }

            var pieces = block.Text.Length <= chunkSize ? [block.Text] : SplitLongText(block.Text, pieceLimit);
            foreach (var piece in pieces)
            {
                builder.Add(piece, block.PageNumber, block.IsHeading);
            }
        }

        return builder.Complete();
    }

    private static IEnumerable<TextBlock> ReadBlocks(IEnumerable<PageText> pages)
    {
        foreach (var page in pages)
        {
            var paragraph = new StringBuilder();
            foreach (var rawLine in page.Text.ReplaceLineEndings("\n").Split('\n'))
            {
                var line = rawLine.Trim();
                var isHeading = IsHeading(line);

                if ((line.Length == 0 || isHeading) && paragraph.Length > 0)
                {
                    yield return new TextBlock(paragraph.ToString(), page.PageNumber, IsHeading: false);
                    paragraph.Clear();
                }

                if (isHeading)
                {
                    yield return new TextBlock(line, page.PageNumber, IsHeading: true);
                }
                else if (line.Length > 0)
                {
                    paragraph.Append(paragraph.Length > 0 ? "\n" : string.Empty).Append(line);
                }
            }

            if (paragraph.Length > 0)
            {
                yield return new TextBlock(paragraph.ToString(), page.PageNumber, IsHeading: false);
            }
        }
    }

    /// <summary>
    /// A numbered line only counts as a heading when it is too short to be a sentence wrapped onto the next line.
    /// Missing a heading only leaves it inside a chunk; a false one creates a tiny chunk, so the rule is strict.
    /// </summary>
    private static bool IsHeading(string line)
    {
        if (line.Length == 0 || line.Length > MaxHeadingLength)
        {
            return false;
        }

        if (StructuralHeading().IsMatch(line))
        {
            return true;
        }

        if (NumberedHeading().IsMatch(line)
            && line.Length <= MaxNumberedHeadingLength
            && !".;,:".Contains(line[^1], StringComparison.Ordinal))
        {
            return true;
        }

        return IsCapitalizedHeading(line);
    }

    private static bool IsCapitalizedHeading(string line)
    {
        var letters = line.Where(char.IsLetter).ToList();

        // Mostly letters, so page footers with company numbers and formulas do not qualify.
        return letters.Count >= 4
            && letters.All(char.IsUpper)
            && line.Contains(' ', StringComparison.Ordinal)
            && letters.Count * 10 >= line.Count(c => !char.IsWhiteSpace(c)) * 7;
    }

    private static List<string> SplitLongText(string text, int limit)
    {
        var pieces = new List<string>();
        var remaining = text.AsSpan().Trim();

        while (remaining.Length > limit)
        {
            var cut = FindCut(remaining[..limit]);
            pieces.Add(remaining[..cut].TrimEnd().ToString());
            remaining = remaining[cut..].TrimStart();
        }

        if (!remaining.IsEmpty)
        {
            pieces.Add(remaining.ToString());
        }

        return pieces;
    }

    /// <summary>Prefers the end of a sentence, then a space, and only then cuts through a word.</summary>
    private static int FindCut(ReadOnlySpan<char> window)
    {
        for (var i = window.Length - 1; i >= window.Length / 2; i--)
        {
            if (char.IsWhiteSpace(window[i]) && i > 0 && ".!?;".Contains(window[i - 1]))
            {
                return i;
            }
        }

        for (var i = window.Length - 1; i > 0; i--)
        {
            if (char.IsWhiteSpace(window[i]))
            {
                return i;
            }
        }

        // Never leaves half of a surrogate pair at either side of the cut.
        return window.Length > 1 && char.IsHighSurrogate(window[^1]) ? window.Length - 1 : window.Length;
    }

    // Polish letters are optional: some PDFs lose them on the way out.
    [GeneratedRegex(@"^((?i:Rozdzia[łl])\s+([IVXLCDM]+|\d+)\b|§\s*\d+|(?i:Za[łl][ąa]cznik)\s+(?i:nr)\s*\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StructuralHeading();

    [GeneratedRegex(@"^([IVXLCDM]+\.|\d+(\.\d+)*\.|\d+(\.\d+)+)\s+\p{Lu}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeading();

    private sealed record TextBlock(string Text, int PageNumber, bool IsHeading);

    private sealed class ChunkBuilder(int chunkSize, int chunkOverlap)
    {
        private readonly List<DocumentChunk> _chunks = [];
        private readonly StringBuilder _text = new();
        private string? _previous;
        private int? _pageNumber;
        private bool _hasBody;

        /// <summary>Ends the current chunk, unless it only holds headings: those stay with the text they introduce.</summary>
        public void StartSection()
        {
            if (_hasBody)
            {
                Flush();
            }

            if (_text.Length == 0)
            {
                _previous = null;
            }
        }

        public void Add(string text, int pageNumber, bool isHeading)
        {
            if (_text.Length > 0 && _text.Length + Separator.Length + text.Length > chunkSize)
            {
                Flush();
            }

            if (_text.Length == 0)
            {
                AppendOverlap(text.Length);
            }

            _text.Append(_text.Length > 0 ? Separator : string.Empty).Append(text);
            _pageNumber ??= pageNumber;
            _hasBody |= !isHeading;
        }

        public IReadOnlyList<DocumentChunk> Complete()
        {
            Flush();
            return _chunks;
        }

        private void AppendOverlap(int incomingLength)
        {
            if (_previous is null)
            {
                return;
            }

            _text.Append(Tail(_previous, Math.Min(chunkOverlap, chunkSize - incomingLength - Separator.Length)));
        }

        private void Flush()
        {
            if (_pageNumber is not { } pageNumber)
            {
                return;
            }

            _previous = _text.ToString();
            _chunks.Add(new DocumentChunk(_chunks.Count, pageNumber, _previous));
            _text.Clear();
            _pageNumber = null;
            _hasBody = false;
        }

        /// <summary>The end of <paramref name="text"/>, at most <paramref name="maxLength"/> long, starting on a word.</summary>
        private static string Tail(string text, int maxLength)
        {
            if (maxLength <= 0)
            {
                return string.Empty;
            }

            if (text.Length <= maxLength)
            {
                return text;
            }

            var start = text.Length - maxLength;
            if (char.IsWhiteSpace(text[start - 1]))
            {
                return text[start..].Trim();
            }

            var nextSpace = text.AsSpan(start).IndexOfAny(" \n");
            return nextSpace < 0 ? string.Empty : text[(start + nextSpace)..].Trim();
        }
    }
}
