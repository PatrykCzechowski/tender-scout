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
        int timeoutSeconds = 90) =>
        new ChatModelOptionsValidator().Validate(null, new ChatModelOptions
        {
            Endpoint = endpoint,
            Model = model,
            RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        });
}
