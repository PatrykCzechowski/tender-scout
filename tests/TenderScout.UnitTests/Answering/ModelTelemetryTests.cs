using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Answering;

/// <summary>
/// The spans and metrics of the registered chat and embedding clients, as production builds them, collected by the
/// OpenTelemetry SDK in memory. The network is replaced; each test uses a model name of its own, because the clients
/// of other tests write to the same source at the same time.
/// </summary>
public sealed class ModelTelemetryTests
{
    private const string GenAi = "Experimental.Microsoft.Extensions.AI";
    private const string Secret = "Jan Kowalski, PESEL 85010112345";

    private readonly string _model = $"model-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenRouterChat_IsOneClientSpanWithModelEndpointAndTokens_AndNoText()
    {
        using var telemetry = new Telemetry();
        await using var provider = OpenRouter(new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer($"Odpowiedź dla {Secret}")));

        await provider.GetRequiredService<IChatClient>().GetResponseAsync($"Pytanie o {Secret}", cancellationToken: Ct);

        var span = Assert.Single(telemetry.SpansOf(_model));
        Assert.Equal($"chat {_model}", span.DisplayName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("chat", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("openai", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("openrouter.test", span.GetTagItem("server.address"));
        Assert.Equal(10, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(5, span.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.DoesNotContain(Secret, Telemetry.Exported(span), StringComparison.Ordinal);
        Assert.Equal([("input", 10.0), ("output", 5.0)], telemetry.Tokens(_model));
        Assert.Equal(1, telemetry.Operations(_model));
    }

    [Fact]
    public async Task OllamaChat_IsNamedOllama_WithTheTokensOllamaReports()
    {
        using var telemetry = new Telemetry();
        await using var provider = Ollama(new FakeOllamaHandler());

        await provider.GetRequiredService<IChatClient>().GetResponseAsync("Pytanie", cancellationToken: Ct);

        var span = Assert.Single(telemetry.SpansOf(_model));
        Assert.Equal("ollama", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal(FakeOllamaHandler.PromptTokens, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(FakeOllamaHandler.AnswerTokens, span.GetTagItem("gen_ai.usage.output_tokens"));
    }

    [Fact]
    public async Task ReplyWithoutUsage_HasItsDuration_ButNoTokensAtAll()
    {
        using var telemetry = new Telemetry();
        await using var provider = OpenRouter(new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("Tak", usage: false)));

        await provider.GetRequiredService<IChatClient>().GetResponseAsync("Pytanie", cancellationToken: Ct);

        var span = Assert.Single(telemetry.SpansOf(_model));
        Assert.Null(span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Null(span.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Empty(telemetry.Tokens(_model));
        Assert.Equal(1, telemetry.Operations(_model));
    }

    [Fact]
    public async Task EmbeddingBatch_IsOneClientSpanWithInputTokensOnly_AndNoTextOrVector()
    {
        using var telemetry = new Telemetry();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(Settings(new()
        {
            ["Embedding:Endpoint"] = "http://ollama.test:11434",
            ["Embedding:Model"] = _model,
        }));
        services.AddChunkEmbeddings().ConfigurePrimaryHttpMessageHandler(() => new FakeOllamaHandler());
        await using var provider = services.BuildServiceProvider();

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await generator.GenerateAsync([$"Fragment o {Secret}", "Drugi fragment"], cancellationToken: Ct);

        var span = Assert.Single(telemetry.SpansOf(_model));
        Assert.Equal($"embeddings {_model}", span.DisplayName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("ollama", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal(FakeOllamaHandler.PromptTokens, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Null(span.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.DoesNotContain(Secret, Telemetry.Exported(span), StringComparison.Ordinal);
        Assert.DoesNotContain("0.5", Telemetry.Exported(span), StringComparison.Ordinal);
        Assert.Equal([("input", (double)FakeOllamaHandler.PromptTokens)], telemetry.Tokens(_model));
        Assert.False(generator.GetService<OpenTelemetryEmbeddingGenerator<string, Embedding<float>>>()!.EnableSensitiveData);
    }

    [Fact]
    public async Task CaptureSwitchedOnInTheEnvironment_StillRecordsNoText()
    {
        using var telemetry = new Telemetry();
        await using var provider = OpenRouter(new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer($"Odpowiedź dla {Secret}")));
        var client = provider.GetRequiredService<IChatClient>();

        await client.GetResponseAsync($"Pytanie o {Secret}", cancellationToken: Ct);

        // The switch is on in this process (see CaptureSwitchedOn): a client left at the default would record the texts.
        using var byDefault = new OpenTelemetryChatClient(client);
        Assert.True(byDefault.EnableSensitiveData);
        Assert.False(client.GetService<OpenTelemetryChatClient>()!.EnableSensitiveData);
        Assert.DoesNotContain(Secret, Telemetry.Exported(Assert.Single(telemetry.SpansOf(_model))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutSpans_TheMetricsAreStillRecorded()
    {
        using var telemetry = new Telemetry(spans: false);
        await using var provider = OpenRouter(new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("Tak")));

        await provider.GetRequiredService<IChatClient>().GetResponseAsync("Pytanie", cancellationToken: Ct);

        Assert.Equal([("input", 10.0), ("output", 5.0)], telemetry.Tokens(_model));
        Assert.Equal(1, telemetry.Operations(_model));
    }

    [Fact]
    public async Task ProviderError_EndsTheSpanAsAnError_WithoutTheProvidersText()
    {
        using var telemetry = new Telemetry();
        await using var provider = OpenRouter(new FakeOpenRouterHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                new JsonObject { ["error"] = new JsonObject { ["message"] = Secret } }.ToJsonString(), Encoding.UTF8, "application/json"),
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetRequiredService<IChatClient>().GetResponseAsync("Pytanie", cancellationToken: Ct));

        var span = Assert.Single(telemetry.SpansOf(_model));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(HttpRequestException).FullName, span.GetTagItem("error.type"));
        Assert.DoesNotContain(Secret, Telemetry.Exported(span), StringComparison.Ordinal);
        Assert.Equal(1, telemetry.Operations(_model));
    }

    private ServiceProvider OpenRouter(FakeOpenRouterHandler handler) => Chat(handler, new()
    {
        ["Chat:Provider"] = "OpenRouter",
        ["Chat:Endpoint"] = "https://openrouter.test/api/v1",
        [ChatModelOptions.ApiKeySetting] = "sk-or-test-telemetry",
    });

    private ServiceProvider Ollama(HttpMessageHandler handler) => Chat(handler, new() { ["Chat:Endpoint"] = "http://ollama.test:11434" });

    private ServiceProvider Chat(HttpMessageHandler handler, Dictionary<string, string?> settings)
    {
        settings["Chat:Model"] = _model;
        var services = new ServiceCollection().AddSingleton<IConfiguration>(Settings(settings));
        services.AddQuestionAnswering().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Settings(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>The SDK's in-memory exporters for the source and meter of Microsoft.Extensions.AI.</summary>
    private sealed class Telemetry : IDisposable
    {
        private readonly List<Activity> _spans = [];
        private readonly List<MetricSnapshot> _metrics = [];
        private readonly TracerProvider? _tracing;
        private readonly MeterProvider _meters;

        public Telemetry(bool spans = true)
        {
            _tracing = spans ? Sdk.CreateTracerProviderBuilder().AddSource(GenAi).AddInMemoryExporter(_spans).Build() : null;
            _meters = Sdk.CreateMeterProviderBuilder().AddMeter(GenAi).AddInMemoryExporter(_metrics).Build();
        }

        /// <summary>Every name, tag, event and status text of the span, as an exporter would see them.</summary>
        public static string Exported(Activity span) => string.Join(
            '\n',
            [span.DisplayName, span.StatusDescription, .. span.Tags.Select(t => $"{t.Key}={t.Value}"),
             .. span.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}:{t.Key}={t.Value}"))]);

        public List<Activity> SpansOf(string model)
        {
            _tracing?.ForceFlush();
            lock (_spans)
            {
                return [.. _spans.Where(s => (string?)s.GetTagItem("gen_ai.request.model") == model)];
            }
        }

        /// <summary>Sum of each token type of the model, from <c>gen_ai.client.token.usage</c>.</summary>
        public List<(string? Type, double Sum)> Tokens(string model) =>
            [.. Points("gen_ai.client.token.usage", model).Select(p => (Tag(p, "gen_ai.token.type"), p.GetHistogramSum())).OrderBy(t => t.Item1)];

        /// <summary>Number of operations of the model, from <c>gen_ai.client.operation.duration</c>.</summary>
        public long Operations(string model) => Points("gen_ai.client.operation.duration", model).Sum(p => p.GetHistogramCount());

        public void Dispose()
        {
            _tracing?.Dispose();
            _meters.Dispose();
        }

        private static string? Tag(MetricPoint point, string key)
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == key)
                {
                    return tag.Value?.ToString();
                }
            }

            return null;
        }

        private List<MetricPoint> Points(string name, string model)
        {
            _meters.ForceFlush();
            var latest = _metrics.LastOrDefault(m => m.Name == name);
            return latest is null ? [] : [.. latest.MetricPoints.Where(p => Tag(p, "gen_ai.request.model") == model)];
        }
    }
}
