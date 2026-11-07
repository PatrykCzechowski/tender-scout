using System.Globalization;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Ted;

/// <summary>
/// Maps TED notices to <see cref="Notice"/>. Optional fields fall back to <see langword="null"/> (or empty);
/// only a missing publication number or publication date is treated as a broken response.
/// </summary>
internal static class TedNoticeMapper
{
    /// <summary>Languages tried in order for multilingual fields before falling back to any available one.</summary>
    private static readonly string[] PreferredLanguages = ["pol", "eng"];

    internal static Notice Map(TedNoticeDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.PublicationNumber))
        {
            throw new TedApiException("TED returned a notice without a publication number.");
        }

        var publicationDate = ParseDate(dto.PublicationDate)
            ?? throw new TedApiException(
                $"TED notice {dto.PublicationNumber} has a missing or invalid publication date '{dto.PublicationDate}'.");

        var cpvCodes = (dto.CpvCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new Notice(
            PublicationNumber: dto.PublicationNumber.Trim(),
            Title: NullIfBlank(PickLanguage(dto.Title)),
            BuyerName: NullIfBlank(PickLanguage(dto.BuyerName)?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))),
            MainCpvCode: NullIfBlank(dto.MainCpvCode?.FirstOrDefault(code => !string.IsNullOrWhiteSpace(code))),
            CpvCodes: cpvCodes,
            PublicationDate: publicationDate,
            SubmissionDeadline: dto.TenderDeadlines?.Select(ParseDate).Where(date => date is not null).Min(),
            Link: ParseLink(dto.Links));
    }

    /// <summary>
    /// TED dates come as <c>yyyy-MM-dd</c> followed by a UTC offset, e.g. <c>2026-10-20+02:00</c>;
    /// the calendar date is what matters, so the offset is ignored.
    /// </summary>
    private static DateOnly? ParseDate(string? value) =>
        value is { Length: >= 10 }
        && DateOnly.TryParseExact(value.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static Uri? ParseLink(Dictionary<string, Dictionary<string, string>>? links)
    {
        var html = links?.GetValueOrDefault("html");
        return Uri.TryCreate(PickLanguage(html), UriKind.Absolute, out var uri) ? uri : null;
    }

    private static T? PickLanguage<T>(Dictionary<string, T>? values)
        where T : class
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        foreach (var language in PreferredLanguages)
        {
            foreach (var (key, value) in values)
            {
                if (string.Equals(key, language, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return values.Values.FirstOrDefault();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
