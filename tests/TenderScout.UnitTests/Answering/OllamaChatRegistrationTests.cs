using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Answering;

namespace TenderScout.UnitTests.Answering;

/// <summary>The registered chat client, as production builds it, with the network replaced.</summary>
public sealed class OllamaChatRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Client_SendsModelSchemaTemperatureAndContextSize()
    {
        var handler = new FakeOllamaChatHandler();
        await using var provider = Create(handler);

        var response = await Client(provider).GetResponseAsync("Pytanie", QuestionAnswerer.CreateChatOptions(), Ct);

        Assert.Equal(FakeOllamaChatHandler.Reply, response.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://ollama.test:11434/api/chat", request.Uri);
        Assert.Equal("test-model", request.Body["model"]!.GetValue<string>());
        Assert.Equal(0, request.Body["options"]!["temperature"]!.GetValue<double>());
        Assert.Equal(8192, request.Body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(["answer", "citations"], request.Body["format"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Client_IsNotLimitedByTheServiceDefaultsTimeouts()
    {
        var handler = new FakeOllamaChatHandler { Delay = TimeSpan.FromMilliseconds(400) };
        await using var provider = Create(handler, tightDefaults: true);

        var response = await Client(provider).GetResponseAsync("Pytanie", cancellationToken: Ct);

        Assert.Equal(FakeOllamaChatHandler.Reply, response.Text);
    }

    [Fact]
    public async Task Failure_IsNotRetried()
    {
        var handler = new FakeOllamaChatHandler { Status = HttpStatusCode.ServiceUnavailable };
        await using var provider = Create(handler);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Client(provider).GetResponseAsync("Pytanie", cancellationToken: Ct));

        Assert.True(QuestionAnswerer.IsUnavailable(exception, Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SlowerThanTheConfiguredTimeout_FailsAsUnavailable()
    {
        var handler = new FakeOllamaChatHandler { Delay = TimeSpan.FromSeconds(30) };
        await using var provider = Create(handler, new() { ["Chat:RequestTimeout"] = "00:00:00.100" });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Client(provider).GetResponseAsync("Pytanie", cancellationToken: Ct));

        Assert.True(QuestionAnswerer.IsUnavailable(exception, Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InvalidSettings_FailTheStartupValidation()
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddQuestionAnswering();
        await using var provider = services.BuildServiceProvider();

        var exception = Assert.ThrowsAny<Exception>(provider.GetRequiredService<IStartupValidator>().Validate);

        Assert.Contains("Chat:Endpoint", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Chat:Model", exception.ToString(), StringComparison.Ordinal);
    }

    private static IChatClient Client(IServiceProvider provider) => provider.GetRequiredService<IChatClient>();

    private static ServiceProvider Create(
        FakeOllamaChatHandler handler,
        Dictionary<string, string?>? settings = null,
        bool tightDefaults = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Endpoint"] = "http://ollama.test:11434",
                ["Chat:Model"] = "test-model",
            })
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        if (tightDefaults)
        {
            // The handler service defaults add to every client, with limits a slow answer would exceed.
            services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(100);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(1);
            }));
        }

        services.AddQuestionAnswering().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class FakeOllamaChatHandler : HttpMessageHandler
    {
        public const string Reply = """{"answer":"Tak","citations":[1]}""";

        public List<(string Uri, JsonNode Body)> Requests { get; } = [];

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!));

            await Task.Delay(Delay, cancellationToken);

            var body = new JsonObject
            {
                ["model"] = "test-model",
                ["created_at"] = "2026-11-10T12:00:00Z",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply },
                ["done"] = true,
            };
            return new HttpResponseMessage(Status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
