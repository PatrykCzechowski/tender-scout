using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace TenderScout.Infrastructure.Ted;

public static class TedServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITedClient"/> as a typed <see cref="HttpClient"/> configured from the <c>Ted</c>
    /// configuration section, with retries (exponential backoff on 5xx, 408, 429 and timeouts) and timeouts.
    /// </summary>
    public static IHttpClientBuilder AddTedClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TedOptions>()
            .BindConfiguration(TedOptions.SectionName)
            .Validate(options => options.IsValid(), $"Invalid '{TedOptions.SectionName}' configuration.")
            .ValidateOnStart();

        var builder = services.AddHttpClient<ITedClient, TedClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TedOptions>>().Value;
            httpClient.BaseAddress = EnsureTrailingSlash(options.BaseAddress);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Timeouts are owned by the resilience pipeline below.
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Replace the generic pipeline that service defaults add to every HttpClient, so the
        // TED client runs a single pipeline tuned to its own options instead of two stacked ones.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental.
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.AddStandardResilienceHandler()
            .Configure((resilience, serviceProvider) =>
                ConfigureResilience(resilience, serviceProvider.GetRequiredService<IOptions<TedOptions>>().Value));

        return builder;
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions resilience, TedOptions options)
    {
        resilience.Retry.MaxRetryAttempts = options.MaxRetryAttempts;
        resilience.Retry.Delay = options.RetryBaseDelay;
        resilience.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
        resilience.Retry.UseJitter = true;

        resilience.AttemptTimeout.Timeout = options.AttemptTimeout;

        // Every attempt may time out, plus the exponential backoff between them (doubled for jitter headroom).
        var attempts = options.MaxRetryAttempts + 1;
        var backoff = options.RetryBaseDelay * Math.Pow(2, attempts);
        resilience.TotalRequestTimeout.Timeout = (options.AttemptTimeout * attempts) + backoff;

        // The circuit breaker must sample at least two attempt timeouts.
        var minimumSampling = options.AttemptTimeout * 2;
        if (resilience.CircuitBreaker.SamplingDuration < minimumSampling)
        {
            resilience.CircuitBreaker.SamplingDuration = minimumSampling;
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
