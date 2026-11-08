using TenderScout.Infrastructure.Embeddings;

namespace TenderScout.DocumentEmbedding;

/// <summary>
/// Runs one embedding pass, then stops the host. The exit code is non-zero when the pass failed.
/// </summary>
public sealed partial class DocumentEmbeddingWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime lifetime,
    ILogger<DocumentEmbeddingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ChunkEmbedder>().RunAsync(stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Embedding stopped; run it again to embed the remaining chunks")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
