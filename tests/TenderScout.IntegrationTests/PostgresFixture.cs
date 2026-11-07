using Microsoft.EntityFrameworkCore;
using TenderScout.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(TenderScout.IntegrationTests.PostgresFixture))]

namespace TenderScout.IntegrationTests;

/// <summary>
/// One PostgreSQL container for the whole test run, migrated with the committed EF Core migrations.
/// Uses the same image as the AppHost so tests run against the production extension set.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public TenderScoutDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<TenderScoutDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
