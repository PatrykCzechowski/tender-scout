using System.Text.Json.Serialization;

namespace TenderScout.Infrastructure.Ted;

/// <summary>Body of <c>POST /v3/notices/search</c>.</summary>
internal sealed record TedSearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("fields")] IReadOnlyList<string> Fields,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("paginationMode")] string PaginationMode,
    [property: JsonPropertyName("iterationNextToken")] string? IterationNextToken);

/// <summary>Response of <c>POST /v3/notices/search</c>.</summary>
internal sealed record TedSearchResponse(
    [property: JsonPropertyName("notices")] IReadOnlyList<TedNoticeDto>? Notices,
    [property: JsonPropertyName("totalNoticeCount")] long? TotalNoticeCount,
    [property: JsonPropertyName("iterationNextToken")] string? IterationNextToken,
    [property: JsonPropertyName("timedOut")] bool TimedOut);

/// <summary>
/// A notice as returned by TED. Multilingual fields are keyed by language code
/// (<c>pol</c>, <c>eng</c> for texts; <c>POL</c>, <c>ENG</c> for links).
/// </summary>
internal sealed record TedNoticeDto(
    [property: JsonPropertyName(TedFields.PublicationNumber)] string? PublicationNumber,
    [property: JsonPropertyName(TedFields.Title)] Dictionary<string, string>? Title,
    [property: JsonPropertyName(TedFields.BuyerName)] Dictionary<string, List<string>>? BuyerName,
    [property: JsonPropertyName(TedFields.CpvCodes)] List<string>? CpvCodes,
    [property: JsonPropertyName(TedFields.MainCpvCode)] List<string>? MainCpvCode,
    [property: JsonPropertyName(TedFields.PublicationDate)] string? PublicationDate,
    [property: JsonPropertyName(TedFields.TenderDeadlines)] List<string>? TenderDeadlines,
    [property: JsonPropertyName(TedFields.Links)] Dictionary<string, Dictionary<string, string>>? Links);

/// <summary>TED field names requested in every search.</summary>
internal static class TedFields
{
    internal const string PublicationNumber = "publication-number";
    internal const string Title = "notice-title";
    internal const string BuyerName = "buyer-name";
    internal const string CpvCodes = "classification-cpv";
    internal const string MainCpvCode = "main-classification-proc";
    internal const string PublicationDate = "publication-date";
    internal const string TenderDeadlines = "deadline-receipt-tender-date-lot";
    internal const string Links = "links";

    internal static readonly IReadOnlyList<string> All =
        [PublicationNumber, Title, BuyerName, CpvCodes, MainCpvCode, PublicationDate, TenderDeadlines, Links];
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TedSearchRequest))]
[JsonSerializable(typeof(TedSearchResponse))]
internal sealed partial class TedJsonContext : JsonSerializerContext;
