using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Answering;

namespace TenderScout.UnitTests.Answering;

public sealed class ChatModelOptionsValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:11434")]
    [InlineData("/api/chat")]
    [InlineData("ftp://ollama.test")]
    public void Endpoint_MustBeAnAbsoluteHttpAddress(string endpoint)
    {
        Assert.Contains("Chat:Endpoint", Validate(endpoint: endpoint).FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Model_IsRequired(string model)
    {
        Assert.Contains("Chat:Model", Validate(model: model).FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequestTimeout_MustBePositive(int seconds)
    {
        Assert.Contains("Chat:RequestTimeout", Validate(timeoutSeconds: seconds).FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Azure")]
    public void Provider_MustBeOllamaOrOpenRouter(string provider)
    {
        Assert.Contains("Chat:Provider", Validate(provider: provider).FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("OPENROUTER")]
    public void Provider_IgnoresCase(string provider)
    {
        Assert.True(Validate(provider: provider, endpoint: "https://openrouter.test/api/v1", apiKey: "sk-or-test").Succeeded);
    }

    [Theory]
    [InlineData("http://openrouter.test/api/v1")]
    [InlineData("https://user:secret@openrouter.test/api/v1")]
    public void OpenRouterEndpoint_MustBeHttpsWithoutCredentials(string endpoint)
    {
        var result = Validate(provider: "OpenRouter", endpoint: endpoint, apiKey: "sk-or-test");

        Assert.Contains("Chat:Endpoint", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void OpenRouter_NeedsTheKey_AndTheMessageNamesOnlyTheSetting(string? apiKey)
    {
        var result = Validate(provider: "OpenRouter", endpoint: "https://openrouter.test/api/v1", apiKey: apiKey);

        Assert.Equal(["OPENROUTER_API_KEY is required when Chat:Provider is OpenRouter."], result.Failures);
    }

    [Fact]
    public void Ollama_NeedsNoKey()
    {
        Assert.True(Validate(provider: "Ollama", apiKey: null).Succeeded);
    }

    [Fact]
    public void AllInvalidKeys_AreNamed()
    {
        Assert.Equal(3, Validate(endpoint: "", model: "", timeoutSeconds: 0).Failures!.Count());
    }

    [Fact]
    public void ValidSettings_PassWithoutContactingTheEndpoint()
    {
        // Nothing listens on this port; validation must not try.
        Assert.True(Validate(endpoint: "http://127.0.0.1:59999").Succeeded);
    }

    private static ValidateOptionsResult Validate(
        string endpoint = "http://ollama.test:11434",
        string model = "SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0",
        int timeoutSeconds = 90,
        string provider = "Ollama",
        string? apiKey = null) =>
        new ChatModelOptionsValidator().Validate(null, new ChatModelOptions
        {
            Provider = provider,
            Endpoint = endpoint,
            Model = model,
            RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            ApiKey = apiKey,
        });
}
