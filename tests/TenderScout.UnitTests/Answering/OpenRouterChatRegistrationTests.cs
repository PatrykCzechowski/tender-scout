using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Answering;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Answering;

/// <summary>The registered chat client with OpenRouter selected, as production builds it, with the network replaced.</summary>
public sealed class OpenRouterChatRegistrationTests
{
    private const string FakeKey = "sk-or-test-7f3a9c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Answer_GoesToOpenRouterWithTheKeyModelSchemaAndLimits_AndNoOllamaOption()
    {
        var handler = new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("""{"answer":"Tak","citations":[1]}"""));
        await using var provider = Create(handler);

        var response = await Client(provider).GetResponseAsync("Pytanie", QuestionAnswerer.CreateChatOptions(), Ct);

        Assert.Equal("""{"answer":"Tak","citations":[1]}""", response.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://openrouter.test/api/v1/chat/completions", request.Uri);
        Assert.Equal($"Bearer {FakeKey}", request.Authorization);
        Assert.Equal("ask-model", request.Body["model"]!.GetValue<string>());
        Assert.Equal(0, request.Body["temperature"]!.GetValue<double>());
        Assert.Equal(512, MaxTokens(request.Body));
        Assert.Equal("json_schema", request.Body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal(["answer", "citations"], request.Body["response_format"]!["json_schema"]!["schema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain("num_ctx", request.Body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelOfTheRequest_ReplacesTheConfiguredModel()
    {
        var handler = new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("Tak"));
        await using var provider = Create(handler);

        await Client(provider).GetResponseAsync("Pytanie", new ChatOptions { ModelId = "agent-model" }, Ct);

        Assert.Equal("agent-model", Assert.Single(handler.Requests).Body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolCall_IsRunLocallyAndItsResultGoesBackWithTheSameId()
    {
        var handler = new FakeOpenRouterHandler(
            FakeOpenRouterHandler.ToolCall("call_42", "search_notices", """{"query":"ambulans"}"""),
            FakeOpenRouterHandler.Answer("GOTOWE"));
        await using var provider = Create(handler);
        string? received = null;
        var tool = AIFunctionFactory.Create((string query) => received = $"wynik dla {query}", "search_notices");
        using var client = new ChatClientBuilder(Client(provider)).UseFunctionInvocation().Build();

        var response = await client.GetResponseAsync("Pytanie", new ChatOptions { Tools = [tool] }, Ct);

        Assert.Equal("GOTOWE", response.Text);
        Assert.Equal("wynik dla ambulans", received);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("search_notices", handler.Requests[0].Body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        var messages = handler.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal("call_42", messages[^2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[^1]!["role"]!.GetValue<string>());
        Assert.Equal("call_42", messages[^1]!["tool_call_id"]!.GetValue<string>());
        Assert.Contains("wynik dla ambulans", messages[^1]!["content"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProviderError_IsUnavailable_IsNotRetried_AndDoesNotCarryTheProvidersBody(HttpStatusCode status)
    {
        // An error body that echoes the key, as a careless upstream might.
        var handler = new FakeOpenRouterHandler(new HttpResponseMessage(status)
        {
            Content = new StringContent(
                new JsonObject { ["error"] = new JsonObject { ["message"] = $"bad key {FakeKey}", ["code"] = (int)status } }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        });
        await using var provider = Create(handler);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Client(provider).GetResponseAsync("Pytanie", cancellationToken: Ct));

        Assert.True(QuestionAnswerer.IsUnavailable(exception, Ct));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(FakeKey, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bad key", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowerThanTheConfiguredTimeout_IsUnavailable_AndNotRetried()
    {
        var handler = new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("Tak")) { Delay = TimeSpan.FromSeconds(30) };
        await using var provider = Create(handler, new() { ["Chat:RequestTimeout"] = "00:00:00.100" });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Client(provider).GetResponseAsync("Pytanie", cancellationToken: Ct));

        Assert.True(QuestionAnswerer.IsUnavailable(exception, Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallerCancelling_IsNotUnavailable()
    {
        var handler = new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("Tak")) { Delay = TimeSpan.FromSeconds(30) };
        await using var provider = Create(handler);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(provider).GetResponseAsync("Pytanie", cancellationToken: caller.Token));

        Assert.False(QuestionAnswerer.IsUnavailable(exception, caller.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingKey_FailsTheStartupValidation_NamingTheSetting(string? key)
    {
        await using var provider = Create(new FakeOpenRouterHandler(), new() { [ChatModelOptions.ApiKeySetting] = key });

        var exception = Assert.ThrowsAny<Exception>(provider.GetRequiredService<IStartupValidator>().Validate);

        Assert.Contains("OPENROUTER_API_KEY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyFromTheEnvironment_WinsOverTheUserSecret()
    {
        var handler = new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("Tak"));
        var configuration = Settings()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ChatModelOptions.ApiKeySetting] = "from-user-secrets" })
            .AddInMemoryCollection(new Dictionary<string, string?> { [ChatModelOptions.ApiKeySetting] = "from-environment" })
            .Build();
        await using var provider = Build(configuration, handler);

        await Client(provider).GetResponseAsync("Pytanie", cancellationToken: Ct);

        Assert.Equal("Bearer from-environment", Assert.Single(handler.Requests).Authorization);
    }

    private static IChatClient Client(IServiceProvider provider) => provider.GetRequiredService<IChatClient>();

    private static int MaxTokens(JsonNode body) =>
        (body["max_completion_tokens"] ?? body["max_tokens"])!.GetValue<int>();

    private static IConfigurationBuilder Settings() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:Provider"] = "openrouter",
            ["Chat:Endpoint"] = "https://openrouter.test/api/v1",
            ["Chat:Model"] = "ask-model",
        });

    private static ServiceProvider Create(FakeOpenRouterHandler handler, Dictionary<string, string?>? settings = null) =>
        Build(Settings()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ChatModelOptions.ApiKeySetting] = FakeKey })
            .AddInMemoryCollection(settings ?? [])
            .Build(), handler);

    private static ServiceProvider Build(IConfiguration configuration, FakeOpenRouterHandler handler)
    {
        var services = new ServiceCollection().AddSingleton(configuration);
        services.AddQuestionAnswering().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }
}
