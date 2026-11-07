using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.MigrationService;

/// <summary>
/// Applies pending EF Core migrations, then stops the host. The AppHost starts the API and worker
/// only after this service has completed successfully.
/// </summary>
public sealed partial class MigrationWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    public const string ActivitySourceName = "TenderScout.Migrations";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrate database", ActivityKind.Client);

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TenderScoutDbContext>();

            // Aspire enables retry-on-failure, so the migration runs inside the execution strategy.
            var strategy = dbContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(
                dbContext,
                static (context, cancellationToken) => context.Database.MigrateAsync(cancellationToken),
                stoppingToken);

            LogMigrated(logger);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            LogFailed(logger, ex);

            // A non-zero exit code keeps dependants from starting against an outdated schema.
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying database migrations failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
