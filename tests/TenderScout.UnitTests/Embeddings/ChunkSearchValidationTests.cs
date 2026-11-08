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
