namespace TenderScout.Infrastructure.Ted;

/// <summary>
/// Settings for the TED (Tenders Electronic Daily) API v3 client, bound from the <c>Ted</c> configuration section.
/// </summary>
public sealed class TedOptions
{
    public const string SectionName = "Ted";

    /// <summary>Maximum page size accepted by the TED search endpoint.</summary>
    public const int MaxPageSize = 250;

    /// <summary>Root address of the TED API.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.ted.europa.eu/");

    /// <summary>Number of notices requested per page.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>How many times a transient failure (5xx, 408, 429, timeout) is retried.</summary>
    public int MaxRetryAttempts { get; set; } = 4;

    /// <summary>Base delay of the exponential retry backoff.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Timeout of a single HTTP attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal bool IsValid() =>
        BaseAddress is { IsAbsoluteUri: true }
        && PageSize is >= 1 and <= MaxPageSize
        && MaxRetryAttempts is >= 1 and <= 10
        && RetryBaseDelay >= TimeSpan.Zero
        && RetryBaseDelay <= TimeSpan.FromMinutes(1)
        && AttemptTimeout >= TimeSpan.FromMilliseconds(10)
        && AttemptTimeout <= TimeSpan.FromMinutes(5);
}
