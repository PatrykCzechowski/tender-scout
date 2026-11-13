using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Infrastructure.Tools;

/// <summary>The notices <c>search_notices</c> found, newest publication first.</summary>
public sealed record NoticeSearchResult(IReadOnlyList<NoticeSummary> Notices);

/// <summary>A notice in a list: every field of <see cref="Notice"/> except the full list of CPV codes.</summary>
public sealed record NoticeSummary(
    string PublicationNumber,
    string? Title,
    string? BuyerName,
    string? MainCpvCode,
    DateOnly PublicationDate,
    DateOnly? SubmissionDeadline,
    Uri? Link);

/// <summary>The answer of <c>get_notice</c>: the notice, or <c>found: false</c> with a message.</summary>
public sealed record NoticeResult(bool Found, Notice? Notice, string? Message);

/// <summary>The passages <c>search_tender_documents</c> found, best first.</summary>
public sealed record PassageSearchResult(IReadOnlyList<Passage> Passages);

/// <summary>A stored chunk of a tender document, as it is stored.</summary>
public sealed record Passage(string DocId, string PublicationNumber, int Page, string Text);

/// <summary>
/// The tools a model can call to read notices and tender documents. Name, description, parameters, validation and
/// result of each tool are defined here and nowhere else: the MCP host only serves them, and other code in the
/// solution can call the same methods directly. They only read.
/// </summary>
/// <remarks>
/// Invalid input and an unreachable embedding model are reported with <see cref="McpException"/>, the one exception
/// whose message is passed on to the caller, so a model can correct its call.
/// </remarks>
[McpServerToolType]
public sealed partial class TenderTools(
    NoticeQueries notices,
    ChunkEmbeddingRepository chunks,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<EmbeddingOptions> embeddingOptions)
{
    internal const int DefaultNoticeLimit = 10;
    internal const int MaxNoticeLimit = 25;
    internal const int MaxNoticeQueryLength = 200;

    internal const int DefaultPassageLimit = 5;
    internal const int MaxPassageLimit = 10;
    internal const int MaxDocumentQueryLength = 1000;

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// The MCP defaults, except that a missing value is written as <c>null</c> instead of being left out, so every
    /// field of a result is always present.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } =
        new(McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    [McpServerTool(Name = "search_notices", UseStructuredContent = true, ReadOnly = true)]
    [Description(
        "Searches the stored public procurement notices (tender announcements) and returns them newest first, each " +
        "with its publication number, title, buyer, main CPV code, publication date, submission deadline and link. " +
        "Use it to find tenders by topic, buyer, CPV code or deadline; with no arguments it returns the newest " +
        "notices. It does not read tender documents: use search_tender_documents for their content.")]
    public async Task<NoticeSearchResult> SearchNoticesAsync(
        [Description(
            "Words that must all occur in the notice title or the buyer name. Write them in Polish, the language of " +
            "the notices, for example \"serwery Kraków\". A word also matches inside a longer word, so a word stem " +
            "finds its inflected forms. 1 to 200 characters.")]
        string? query = null,
        [Description(
            "The first 2 to 8 digits of a CPV code, for example 72 for IT services or 72000000. It is compared with " +
            "the main CPV code and with the other CPV codes of a notice.")]
        string? cpvPrefix = null,
        [Description(
            "Earliest submission deadline, a date as yyyy-MM-dd, for example 2026-12-01. Notices without a deadline " +
            "are left out.")]
        string? deadlineFrom = null,
        [Description(
            "Latest submission deadline, a date as yyyy-MM-dd, for example 2026-12-31. It must not be before " +
            "deadlineFrom. Notices without a deadline are left out.")]
        string? deadlineTo = null,
        [Description("Maximum number of notices to return, 1 to 25.")]
        int limit = DefaultNoticeLimit,
        CancellationToken cancellationToken = default)
    {
        var text = NoticeQuery(query);
        var prefix = CpvPrefix(cpvPrefix);
        var from = Date(deadlineFrom, nameof(deadlineFrom));
        var to = Date(deadlineTo, nameof(deadlineTo));
        CheckDeadlineRange(from, to);
        CheckLimit(limit, MaxNoticeLimit);

        var found = await notices.SearchAsync(text, prefix, from, to, limit, cancellationToken).ConfigureAwait(false);
        return new NoticeSearchResult([.. found.Select(n => new NoticeSummary(
            n.PublicationNumber, n.Title, n.BuyerName, n.MainCpvCode, n.PublicationDate, n.SubmissionDeadline, n.Link))]);
    }

    [McpServerTool(Name = "get_notice", UseStructuredContent = true, ReadOnly = true)]
    [Description(
        "Returns one stored public procurement notice by its publication number: title, buyer, main CPV code and " +
        "all CPV codes, publication date, submission deadline and link. Use it when the publication number is " +
        "known, for example from search_notices. When no such notice is stored, found is false and message says so.")]
    public async Task<NoticeResult> GetNoticeAsync(
        [Description(
            "TED publication number of the notice: 1 to 6 digits, a hyphen and the 4-digit year, for example " +
            "123456-2026.")]
        string publicationNumber,
        CancellationToken cancellationToken = default)
    {
        var number = PublicationNumber(publicationNumber, nameof(publicationNumber))
            ?? throw new McpException($"{nameof(publicationNumber)} is required.");

        var notice = await notices.FindAsync(number, cancellationToken).ConfigureAwait(false);
        return notice is null
            ? new NoticeResult(false, null, $"No notice with the publication number {number} is stored.")
            : new NoticeResult(true, notice, null);
    }

    [McpServerTool(Name = "search_tender_documents", UseStructuredContent = true, ReadOnly = true)]
    [Description(
        "Searches the text of the stored tender documents, such as the specification of the terms of a tender " +
        "(SWZ), and returns the best matching passages, each with the document id, the publication number of its " +
        "tender, the page and the passage text. Use it for questions about the terms of a tender: the deposit, the " +
        "award criteria, deadlines or conditions for participation. It returns passages, not an answer, and it " +
        "returns the closest passages even when none of them answers the question.")]
    public async Task<PassageSearchResult> SearchTenderDocumentsAsync(
        [Description(
            "The question or search phrase. Write it in Polish, the language of the documents, for example " +
            "\"Jaka jest wysokość wadium?\". 1 to 1000 characters.")]
        string query,
        [Description(
            "Limits the search to the documents of one tender: its TED publication number, 1 to 6 digits, a hyphen " +
            "and the 4-digit year, for example 123456-2026.")]
        string? publicationNumber = null,
        [Description("Maximum number of passages to return, 1 to 10.")]
        int limit = DefaultPassageLimit,
        CancellationToken cancellationToken = default)
    {
        var text = Query(query, nameof(query), MaxDocumentQueryLength)
            ?? throw new McpException($"{nameof(query)} is required.");
        var number = PublicationNumber(publicationNumber, nameof(publicationNumber));
        CheckLimit(limit, MaxPassageLimit);

        ReadOnlyMemory<float> vector;
        try
        {
            vector = await generator.GenerateVectorAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (QuestionAnswerer.IsUnavailable(exception, cancellationToken))
        {
            // Only the message reaches the caller; the cause stays in the server log.
            throw new McpException("The embedding model is unavailable, so the documents cannot be searched now.", exception);
        }

        var found = await chunks
            .HybridSearchAsync(text, vector, embeddingOptions.Value.Model, limit, number, cancellationToken)
            .ConfigureAwait(false);
        return new PassageSearchResult([.. found.Select(c => new Passage(c.DocId, c.PublicationNumber, c.PageNumber, c.Text))]);
    }

    // The filter rules of search_notices. GET /notices applies the same ones, so they are public; each returns the
    // trimmed or parsed value, or null when the filter was not given, and throws McpException with the message.

    public static string? NoticeQuery(string? value) => Query(value, "query", MaxNoticeQueryLength);

    public static string? CpvPrefix(string? value)
    {
        var prefix = Trimmed(value, "cpvPrefix");
        return prefix is null || CpvPrefixPattern().IsMatch(prefix)
            ? prefix
            : throw new McpException("cpvPrefix must be 2 to 8 digits, for example 72.");
    }

    public static void CheckDeadlineRange(DateOnly? deadlineFrom, DateOnly? deadlineTo)
    {
        if (deadlineTo < deadlineFrom)
        {
            throw new McpException("deadlineTo must not be before deadlineFrom.");
        }
    }

    /// <returns>The trimmed text, or <see langword="null"/> when the parameter was not given.</returns>
    private static string? Trimmed(string? value, string parameter)
    {
        var text = value?.Trim();
        return text?.Length == 0
            ? throw new McpException($"{parameter} must not be empty; leave it out if it is not needed.")
            : text;
    }

    private static string? Query(string? value, string parameter, int maxLength)
    {
        var text = Trimmed(value, parameter);
        return text is null || text.Length <= maxLength
            ? text
            : throw new McpException($"{parameter} must have 1 to {maxLength} characters.");
    }

    private static string? PublicationNumber(string? value, string parameter)
    {
        var number = Trimmed(value, parameter);
        return number is null || PublicationNumberPattern().IsMatch(number)
            ? number
            : throw new McpException($"{parameter} must be 1 to 6 digits, a hyphen and 4 digits, for example 123456-2026.");
    }

    public static DateOnly? Date(string? value, string parameter)
    {
        var text = Trimmed(value, parameter);
        if (text is null)
        {
            return null;
        }

        return DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new McpException($"{parameter} must be a date as {DateFormat}, for example 2026-12-01.");
    }

    private static void CheckLimit(int limit, int max)
    {
        if (limit < 1 || limit > max)
        {
            throw new McpException($"{nameof(limit)} must be between 1 and {max}.");
        }
    }

    // [0-9] and not \d: only ASCII digits count.
    [GeneratedRegex("^[0-9]{2,8}$")]
    private static partial Regex CpvPrefixPattern();

    [GeneratedRegex("^[0-9]{1,6}-[0-9]{4}$")]
    private static partial Regex PublicationNumberPattern();
}
