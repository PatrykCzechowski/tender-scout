using TenderScout.Infrastructure.Persistence;

namespace TenderScout.MigrationService;

/// <summary>
/// Loads the invented demo data that the publish output carries in <c>demo/</c> (see <see cref="DemoData"/>), then
/// stops the host. Started with <see cref="Argument"/> after the migrations; a failure ends with exit code 1.
/// </summary>
public sealed partial class DemoDataWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime lifetime,
    ILogger<DemoDataWorker> logger) : BackgroundService
{
    public const string Argument = "--demo-data";

    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "demo");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TenderScoutDbContext>();
            var (loaded, notices, documents, passages) = await DemoData.LoadAsync(
                dbContext,
                Path.Combine(Folder, "notices.jsonl"),
                Path.Combine(Folder, "synthetic-passages.jsonl"),
                TimeProvider.System.GetUtcNow(),
                stoppingToken);

            if (loaded)
            {
                LogLoaded(logger, notices, documents, passages);
            }
            else
            {
                LogAlreadyLoaded(logger, notices, documents, passages);
            }
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo data loaded: {Notices} notices, {Documents} documents, {Passages} passages")]
    private static partial void LogLoaded(ILogger logger, int notices, int documents, int passages);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo data already loaded ({Notices} notices, {Documents} documents, {Passages} passages); nothing changed")]
    private static partial void LogAlreadyLoaded(ILogger logger, int notices, int documents, int passages);

    [LoggerMessage(Level = LogLevel.Error, Message = "Loading the demo data failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
