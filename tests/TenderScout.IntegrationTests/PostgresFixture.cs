using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Pgvector.EntityFrameworkCore;
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

    public string ConnectionString => _container.GetConnectionString();

    public TenderScoutDbContext CreateDbContext() => CreateDbContext(ConnectionString);

    public TenderScoutDbContext CreateDbContext(string connectionString, bool retryOnFailure = false) =>
        new(new DbContextOptionsBuilder<TenderScoutDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                if (retryOnFailure)
                {
                    npgsql.EnableRetryOnFailure();
                }
            })
            .Options);

    /// <summary>
    /// Creates a database only the calling test sees, migrated up to <paramref name="targetMigration"/> or fully.
    /// </summary>
    /// <returns>Its connection string.</returns>
    public async Task<string> CreateIsolatedDatabaseAsync(string? targetMigration = null)
    {
        var name = $"test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.GetService<IMigrator>().MigrateAsync(targetMigration);
        return connectionString;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
