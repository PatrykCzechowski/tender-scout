using TenderScout.Infrastructure.Documents;

namespace TenderScout.DocumentImport;

/// <summary>
/// Runs one document import, then stops the host. The exit code is non-zero when a row was rejected.
/// </summary>
public sealed partial class DocumentImportWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime lifetime,
    ILogger<DocumentImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var report = await scope.ServiceProvider.GetRequiredService<DocumentImporter>().RunAsync(stoppingToken);

            if (!report.Succeeded)
            {
                LogRejectedRows(logger, report.Rejected.Count);
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Document import finished with {Count} rejected rows")]
    private static partial void LogRejectedRows(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Document import failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
