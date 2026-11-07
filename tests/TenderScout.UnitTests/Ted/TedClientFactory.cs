using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenderScout.Infrastructure.Ted;

namespace TenderScout.UnitTests.Ted;

internal static class TedClientFactory
{
    /// <summary>
    /// Builds the client exactly as production does (DI, options, resilience pipeline),
    /// with the network replaced by <paramref name="handler"/> and near-zero retry delays.
    /// </summary>
    /// <param name="withServiceDefaults">Also add the global resilience handler that <c>AddServiceDefaults</c> puts on every client.</param>
    public static ServiceProvider Create(
        FakeTedHandler handler,
        Dictionary<string, string?>? settings = null,
        bool withServiceDefaults = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ted:BaseAddress"] = "https://ted.test/api",
                ["Ted:PageSize"] = "2",
                ["Ted:RetryBaseDelay"] = "00:00:00.001",
            })
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (withServiceDefaults)
        {
            services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        }

        services.AddTedClient().ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
