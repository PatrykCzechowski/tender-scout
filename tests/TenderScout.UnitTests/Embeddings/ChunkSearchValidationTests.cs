using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.UnitTests.Embeddings;

/// <summary>Nothing listens on this connection string: these requests must fail before reaching a database.</summary>
public sealed class ChunkSearchValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(41)]
    public async Task Search_LimitOutOfRange_IsRejected(int limit)
    {
        await using var dbContext = CreateDbContext();
        var repository = new ChunkEmbeddingRepository(dbContext);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.SearchAsync(new float[ChunkEmbedding.Dimensions], "bge-m3", limit, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1536)]
    public async Task Search_WrongVectorLength_NamesTheRequiredDimension(int length)
    {
        await using var dbContext = CreateDbContext();
        var repository = new ChunkEmbeddingRepository(dbContext);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => repository.SearchAsync(new float[length], "bge-m3", 5, Ct));

        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task HybridSearch_LimitOutOfRange_IsRejected(int limit)
    {
        await using var dbContext = CreateDbContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new ChunkEmbeddingRepository(dbContext).HybridSearchAsync("wadium", new float[ChunkEmbedding.Dimensions], "bge-m3", limit, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HybridSearch_BlankText_IsRejected(string text)
    {
        await using var dbContext = CreateDbContext();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new ChunkEmbeddingRepository(dbContext).HybridSearchAsync(text, new float[ChunkEmbedding.Dimensions], "bge-m3", 5, Ct));
    }

    [Fact]
    public async Task HybridSearch_WrongVectorLength_IsRejected()
    {
        await using var dbContext = CreateDbContext();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => new ChunkEmbeddingRepository(dbContext).HybridSearchAsync("wadium", new float[3], "bge-m3", 5, Ct));

        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jaka jest wysokość wadium?", "wysokość or wadium")]
    [InlineData("-wadium \"termin", "wadium or termin")]
    [InlineData("CPV 72000000-5", "cpv or 72000000")]
    [InlineData("Wadium, wadium!", "wadium")]
    public void ToTextQuery_KeepsContentWordsJoinedWithOr(string question, string expected)
    {
        Assert.Equal(expected, ChunkEmbeddingRepository.ToTextQuery(question));
    }

    [Theory]
    [InlineData("Jaka jest?")]
    [InlineData("?! ... - \"")]
    [InlineData("to na do")]
    public void ToTextQuery_NoContentWords_GivesNull(string question)
    {
        Assert.Null(ChunkEmbeddingRepository.ToTextQuery(question));
    }

    [Fact]
    public async Task UsePgvector_KeepsConnectionAndRetrySettingsAlreadyConfigured()
    {
        var options = new DbContextOptionsBuilder<TenderScoutDbContext>();
        options.UseNpgsql("Host=db.test;Database=tenderscout", npgsql => npgsql.EnableRetryOnFailure());

        options.UsePgvector();

        await using var dbContext = new TenderScoutDbContext(options.Options);
        Assert.Contains("db.test", dbContext.Database.GetConnectionString(), StringComparison.Ordinal);
        Assert.True(dbContext.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.NotNull(dbContext.Model.FindEntityType(typeof(DocumentChunkEntity)));
    }

    private static TenderScoutDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<TenderScoutDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none", npgsql => npgsql.UseVector())
            .Options);
}
