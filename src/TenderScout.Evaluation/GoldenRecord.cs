using System.Text.Json;
using System.Text.RegularExpressions;

namespace TenderScout.Evaluation;

/// <summary>
/// A golden-set question and where its answer is, one JSON line in snake_case; the format is described in
/// <c>evals/README.md</c>. Unanswerable questions have no document, page or quote.
/// </summary>
/// <param name="Page">One-based page of the quote.</param>
/// <param name="LabelSource"><c>auto</c> until a person has checked the record, then <c>verified</c>.</param>
/// <param name="PublicationNumber">The tender the question is about; search and <c>/ask</c> are limited to it, as in the API.</param>
/// <param name="Vector">Stored query vector; only the synthetic CI set has one, so it runs without a model.</param>
public sealed partial record GoldenRecord(
    string Id,
    string Question,
    bool Answerable,
    bool Paraphrase,
    string? DocId,
    int? Page,
    string? Quote,
    string LabelSource,
    string? PublicationNumber = null,
    float[]? Vector = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static IReadOnlyList<GoldenRecord> Load(string path) =>
        [.. File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<GoldenRecord>(line, JsonOptions)!)];

    /// <summary>Whether a passage of <paramref name="docId"/> contains the quote, ignoring case and whitespace.</summary>
    public bool IsAnsweredBy(string docId, string text) =>
        Answerable && docId == DocId && Normalize(text).Contains(Normalize(Quote!), StringComparison.Ordinal);

    private static string Normalize(string text) => Whitespace().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
