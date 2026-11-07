namespace TenderScout.Infrastructure.Ingestion;

/// <summary>
/// Settings for polling TED and consuming <c>NoticeDiscovered</c>, bound from the <c>Ingestion</c> section.
/// </summary>
public sealed class NoticeIngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Time between two TED polls. The first poll runs at startup.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many days before today each poll searches, today included. Overlapping windows make a missed
    /// or failed poll harmless: the next one finds the same notices again.
    /// </summary>
    public int LookBackDays { get; set; } = 3;

    /// <summary>How many times a message is retried after a transient failure before it goes to the error queue.</summary>
    public int RetryLimit { get; set; } = 5;

    /// <summary>First retry delay; later retries grow exponentially up to <see cref="RetryMaxInterval"/>.</summary>
    public TimeSpan RetryMinInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaxInterval { get; set; } = TimeSpan.FromSeconds(30);

    internal bool IsValid() =>
        PollingInterval >= TimeSpan.FromSeconds(1)
        && LookBackDays is >= 0 and <= 31
        && RetryLimit is >= 0 and <= 20
        && RetryMinInterval >= TimeSpan.Zero
        && RetryMaxInterval >= RetryMinInterval;
}
