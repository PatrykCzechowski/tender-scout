using System.Globalization;

namespace TenderScout.Infrastructure.Ted;

/// <summary>
/// Builds the TED expert-search query for IT tenders published by Polish buyers.
/// </summary>
internal static class TedSearchQuery
{
    /// <summary>ISO 3166-1 alpha-3 code TED uses for buyer countries.</summary>
    internal const string BuyerCountry = "POL";

    /// <summary>Main CPV prefixes: 72 = IT services, 48 = software packages and information systems.</summary>
    internal static readonly string[] MainCpvPrefixes = ["72", "48"];

    internal static string Build(DateOnly from, DateOnly to)
    {
        var cpv = string.Join(" OR ", MainCpvPrefixes.Select(prefix => $"main-classification-proc={prefix}*"));

        return $"buyer-country={BuyerCountry} AND ({cpv}) "
            + $"AND publication-date>={Format(from)} AND publication-date<={Format(to)}";
    }

    private static string Format(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
