using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OllamaSharp;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Infrastructure.Embeddings;

public static class EmbeddingServiceCollectionExtensions
{
    internal const string HttpClientName = "ollama";

    /// <summary>Retries of a batch after a connection error, a timeout or a transient HTTP status.</summary>
    private const int MaxRetryAttempts = 2;

    /// <summary>Delay before the first retry; later ones grow exponentially.</summary>
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>A switched-off machine should fail in seconds, while a loaded model may take minutes to answer.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Registers <see cref="ChunkEmbedder"/>, <see cref="DocumentSearchCommand"/>, their options (<c>Embedding</c> section) and the Ollama-backed
    /// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>. This is the only place that knows about Ollama.
    /// </summary>
    public static IHttpClientBuilder AddChunkEmbeddings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<EmbeddingOptions>()
            .BindConfiguration(EmbeddingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmbeddingOptions>, EmbeddingOptionsValidator>();

        services.AddScoped<ChunkEmbeddingRepository>();
        services.AddScoped<ChunkEmbedder>();
        services.AddScoped<DocumentSearchCommand>();

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(serviceProvider =>
        {
            var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var options = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            return new OllamaApiClient(httpClient, options.Model);
        });

        var builder = services.AddHttpClient(HttpClientName, (serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            httpClient.BaseAddress = new Uri(options.Endpoint);

            // Timeouts are owned by the resilience pipeline below.
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
        });

        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = ConnectTimeout });

        // Service defaults limit every HttpClient to 10 s per attempt and 30 s in total, too short for a cold model.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental.
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.AddStandardResilienceHandler()
            .Configure((resilience, serviceProvider) =>
                ConfigureResilience(resilience, serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value));

        return builder;
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions resilience, EmbeddingOptions options)
    {
        resilience.Retry.MaxRetryAttempts = MaxRetryAttempts;
        resilience.Retry.Delay = RetryBaseDelay;
        resilience.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
        resilience.Retry.UseJitter = true;

        resilience.AttemptTimeout.Timeout = options.RequestTimeout;

        // Every attempt may time out, plus the exponential backoff between them (doubled for jitter headroom).
        var attempts = MaxRetryAttempts + 1;
        var backoff = RetryBaseDelay * Math.Pow(2, attempts);
        resilience.TotalRequestTimeout.Timeout = (options.RequestTimeout * attempts) + backoff;

        // The circuit breaker must sample at least two attempt timeouts.
        var minimumSampling = options.RequestTimeout * 2;
        if (resilience.CircuitBreaker.SamplingDuration < minimumSampling)
        {
            resilience.CircuitBreaker.SamplingDuration = minimumSampling;
        }
    }
}
