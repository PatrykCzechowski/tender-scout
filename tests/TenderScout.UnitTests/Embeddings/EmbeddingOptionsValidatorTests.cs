using TenderScout.Infrastructure.Embeddings;

namespace TenderScout.UnitTests.Embeddings;

public sealed class EmbeddingOptionsValidatorTests
{
    [Fact]
    public void Validate_UnreachableButWellFormedEndpoint_Succeeds()
    {
        var options = CreateOptions();
        options.Endpoint = "http://127.0.0.1:59999";

        Assert.True(new EmbeddingOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/api/embed")]
    [InlineData("localhost:11434")]
    [InlineData("ftp://ollama.test:11434")]
    public void Validate_InvalidEndpoint_NamesTheKey(string endpoint)
    {
        var options = CreateOptions();
        options.Endpoint = endpoint;

        var result = new EmbeddingOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Embedding:Endpoint", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_BlankModel_NamesTheKey(string model)
    {
        var options = CreateOptions();
        options.Model = model;

        var result = new EmbeddingOptionsValidator().Validate(null, options);

        Assert.Contains("Embedding:Model", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveBatchSize_NamesTheKey(int batchSize)
    {
        var options = CreateOptions();
        options.BatchSize = batchSize;

        var result = new EmbeddingOptionsValidator().Validate(null, options);

        Assert.Contains("Embedding:BatchSize", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    public void Validate_NonPositiveTimeout_NamesTheKey(string timeout)
    {
        var options = CreateOptions();
        options.RequestTimeout = TimeSpan.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture);

        var result = new EmbeddingOptionsValidator().Validate(null, options);

        Assert.Contains("Embedding:RequestTimeout", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SeveralProblems_NamesEachKey()
    {
        var result = new EmbeddingOptionsValidator().Validate(null, new EmbeddingOptions { BatchSize = 0 });

        Assert.Contains("Embedding:Endpoint", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Embedding:Model", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Embedding:BatchSize", result.FailureMessage, StringComparison.Ordinal);
    }

    private static EmbeddingOptions CreateOptions() =>
        new()
        {
            Endpoint = "http://ollama.test:11434",
            Model = "bge-m3",
            BatchSize = 32,
        };
}
