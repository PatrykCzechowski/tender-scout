using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the context without starting an app. Generating migrations never
/// connects, so the connection string is only a placeholder.
/// </summary>
internal sealed class TenderScoutDbContextFactory : IDesignTimeDbContextFactory<TenderScoutDbContext>
{
    public TenderScoutDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TenderScoutDbContext>()
            .UseNpgsql("Host=localhost;Database=tenderscout", npgsql => npgsql.UseVector())
            .Options);
}
