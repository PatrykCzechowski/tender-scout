using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Documents;
using TenderScout.Infrastructure.Embeddings;

namespace TenderScout.UnitTests.Embeddings;

/// <summary>The registered generator, as production builds it, with the network replaced.</summary>
public sealed class OllamaEmbeddingRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generator_SendsConfiguredModelToConfiguredEndpoint()
    {
        var handler = new FakeOllamaHandler();
        await using var provider = Create(handler);

        var embeddings = await Generator(provider).GenerateAsync(["Pierwszy", "Drugi"], cancellationToken: Ct);

        Assert.Equal(2, embeddings.Count);
        Assert.Equal(ChunkEmbedding.Dimensions, embeddings[0].Vector.Length);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://ollama.test:11434/api/embed", request.Uri);
        Assert.Equal("test-model", request.Body["model"]!.GetValue<string>());
        Assert.Equal(["Pierwszy", "Drugi"], request.Body["input"]!.AsArray().Select(input => input!.GetValue<string>()));
    }

    [Fact]
    public async Task Generator_TransientFailure_IsRetried()
    {
        var handler = new FakeOllamaHandler();
        handler.Failures.Enqueue(HttpStatusCode.ServiceUnavailable);
        await using var provider = Create(handler);

        await Generator(provider).GenerateAsync(["Tekst"], cancellationToken: Ct);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Generator_ModelNotFound_IsNotRetried()
    {
        var handler = new FakeOllamaHandler();
        handler.Failures.Enqueue(HttpStatusCode.NotFound);
        await using var provider = Create(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => Generator(provider).GenerateAsync(["Tekst"], cancellationToken: Ct));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Generator_IsNotLimitedByTheServiceDefaultsTimeouts()
    {
        var handler = new FakeOllamaHandler { Delay = TimeSpan.FromMilliseconds(400) };
        await using var provider = Create(handler, tightDefaults: true);

        var embeddings = await Generator(provider).GenerateAsync(["Wolna odpowiedź"], cancellationToken: Ct);

        Assert.Single(embeddings);
    }

    [Fact]
    public async Task Generator_SlowerThanTheConfiguredTimeout_Fails()
    {
        var handler = new FakeOllamaHandler { Delay = TimeSpan.FromSeconds(30) };
        await using var provider = Create(handler, new() { ["Embedding:RequestTimeout"] = "00:00:00.050" });

        // How many attempts fit in so short a budget depends on the machine, so only the failure is asserted.
        await Assert.ThrowsAnyAsync<Exception>(() => Generator(provider).GenerateAsync(["Tekst"], cancellationToken: Ct));
    }

    [Fact]
    public async Task InvalidSettings_FailTheStartupValidation()
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddChunkEmbeddings();
        await using var provider = services.BuildServiceProvider();

        var exception = Assert.ThrowsAny<Exception>(provider.GetRequiredService<IStartupValidator>().Validate);

        Assert.Contains("Embedding:Endpoint", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Import_NeedsNoEmbeddingSettings()
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddDocumentImport();

        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IConfigureOptions<EmbeddingOptions>));
    }

    private static IEmbeddingGenerator<string, Embedding<float>> Generator(IServiceProvider provider) =>
        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

    private static ServiceProvider Create(
        FakeOllamaHandler handler,
        Dictionary<string, string?>? settings = null,
        bool tightDefaults = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Endpoint"] = "http://ollama.test:11434",
                ["Embedding:Model"] = "test-model",
                ["Embedding:RetryBaseDelay"] = "00:00:00.001",
            })
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        if (tightDefaults)
        {
            // The handler service defaults add to every client, with limits a slow response would exceed.
            services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(100);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(1);
            }));
        }

        services.AddChunkEmbeddings().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class FakeOllamaHandler : HttpMessageHandler
    {
        public List<(string Uri, JsonNode Body)> Requests { get; } = [];

        public Queue<HttpStatusCode> Failures { get; } = new();

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.AbsoluteUri, body));
            }

            await Task.Delay(Delay, cancellationToken);

            if (Failures.TryDequeue(out var status))
            {
                return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"simulated\"}", Encoding.UTF8, "application/json") };
            }

            var vectors = body["input"]!.AsArray().Select(_ => new float[ChunkEmbedding.Dimensions]).ToArray();
            var json = JsonSerializer.Serialize(new { model = "test-model", embeddings = vectors });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
