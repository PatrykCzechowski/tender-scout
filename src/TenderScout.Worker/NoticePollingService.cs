using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Ingestion;

namespace TenderScout.Worker;

/// <summary>
/// Runs <see cref="NoticeIngestion"/> at startup and then every <see cref="NoticeIngestionOptions.PollingInterval"/>.
/// A failed poll is logged and retried on the next tick; the overlapping look-back window covers the gap.
/// </summary>
public sealed partial class NoticePollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<NoticeIngestionOptions> options,
    TimeProvider timeProvider,
    ILogger<NoticePollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PollingInterval;
        LogStarted(logger, interval, options.Value.LookBackDays);

        using var timer = new PeriodicTimer(interval, timeProvider);
        do
        {
            await PollAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NoticeIngestion>().RunOnceAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogPollFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Polling TED every {Interval}, looking back {LookBackDays} days")]
    private static partial void LogStarted(ILogger logger, TimeSpan interval, int lookBackDays);

    [LoggerMessage(Level = LogLevel.Error, Message = "TED poll failed; retrying on the next tick")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
