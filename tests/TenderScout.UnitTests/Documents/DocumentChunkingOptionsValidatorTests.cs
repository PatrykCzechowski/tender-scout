using TenderScout.Infrastructure.Documents;

namespace TenderScout.UnitTests.Documents;

public sealed class DocumentChunkingOptionsValidatorTests
{
    [Fact]
    public void Validate_Defaults_Succeed()
    {
        Assert.True(new DocumentChunkingOptionsValidator().Validate(null, new DocumentChunkingOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0, 0, "ChunkSize")]
    [InlineData(-5, 0, "ChunkSize")]
    [InlineData(100, -1, "ChunkOverlap")]
    [InlineData(100, 100, "ChunkOverlap")]
    [InlineData(100, 200, "ChunkOverlap")]
    public void Validate_InvalidChunkSettings_NamesTheSetting(int size, int overlap, string setting)
    {
        var options = new DocumentChunkingOptions { ChunkSize = size, ChunkOverlap = overlap };

        var result = new DocumentChunkingOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains($"DocumentChunking:{setting}", result.FailureMessage, StringComparison.Ordinal);
    }
}
