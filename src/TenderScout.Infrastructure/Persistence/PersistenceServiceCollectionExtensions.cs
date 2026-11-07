using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
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
