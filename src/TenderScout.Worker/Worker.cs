namespace TenderScout.Worker;

/// <summary>
/// Placeholder background service; real work arrives in later issues.
/// </summary>
public sealed partial class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "TenderScout worker started")]
    private static partial void LogStarted(ILogger logger);
}
