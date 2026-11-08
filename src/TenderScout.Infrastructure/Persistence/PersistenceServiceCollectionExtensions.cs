using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pgvector.EntityFrameworkCore;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Adds pgvector support to a context already configured for PostgreSQL. Every context needs it,
    /// because the model has a vector column; the connection and retry settings are left as they are.
    /// </summary>
    public static DbContextOptionsBuilder UsePgvector(this DbContextOptionsBuilder options) =>
        options.UseNpgsql(npgsql => npgsql.UseVector());

    /// <summary>
    /// Registers the notice repository. The <see cref="TenderScoutDbContext"/> itself is registered by the
    /// host (e.g. Aspire's <c>AddNpgsqlDbContext</c>), which owns the connection string.
    /// </summary>
    public static IServiceCollection AddNoticePersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<INoticeRepository, NoticeRepository>();

        return services;
    }
}
