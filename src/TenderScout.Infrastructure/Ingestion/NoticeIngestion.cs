using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Ted;

namespace TenderScout.Infrastructure.Ingestion;

/// <summary>
/// One discovery pass: reads the look-back window from TED and publishes a <c>NoticeDiscovered</c> per notice.
/// Storing happens in <see cref="NoticeDiscoveredConsumer"/>, so slow or failing storage never blocks polling.
/// </summary>
public sealed partial class NoticeIngestion(
    ITedClient tedClient,
    IPublishEndpoint publishEndpoint,
    IOptions<NoticeIngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<NoticeIngestion> logger)
{
    /// <returns>The number of notices published.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var from = today.AddDays(-options.Value.LookBackDays);

        var notices = await tedClient.SearchNoticesAsync(from, today, cancellationToken).ConfigureAwait(false);

        foreach (var notice in notices)
        {
            await publishEndpoint.Publish(notice.ToMessage(), cancellationToken).ConfigureAwait(false);
        }

        LogPublished(logger, notices.Count, from, today);
        return notices.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Published {Count} discovered notices for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}")]
    private static partial void LogPublished(ILogger logger, int count, DateOnly from, DateOnly to);
}
