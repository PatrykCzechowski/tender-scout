using Microsoft.EntityFrameworkCore;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Demo;

/// <summary>Loading the compose demo's invented data, each test in a database of its own.</summary>
public sealed class DemoDataTests(PostgresFixture database)
{
    private static readonly string Notices = Path.Combine(AppContext.BaseDirectory, "Evaluation", "agent", "notices.jsonl");
    private static readonly string Passages = Path.Combine(AppContext.BaseDirectory, "Evaluation", "synthetic-passages.jsonl");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyDatabase_GetsEveryNoticeDocumentAndPassageWithItsVector()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();

        var result = await LoadAsync(connection);

        Assert.Equal((true, 10, 5, 30), result);
        await using var dbContext = database.CreateDbContext(connection);
        Assert.Equal(10, await dbContext.Notices.CountAsync(Ct));
        var chunks = await dbContext.Documents.SelectMany(d => d.Chunks).ToListAsync(Ct);
        Assert.Equal(30, chunks.Count);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(1024, chunk.Embedding!.ToArray().Length);
            Assert.Equal("bge-m3", chunk.EmbeddingModel);
        });
        Assert.Equal(
            File.ReadLines(Notices).Count(line => !string.IsNullOrWhiteSpace(line)),
            await dbContext.Notices.CountAsync(n => n.Title != null && n.SubmissionDeadline != null, Ct));
    }

    [Fact]
    public async Task SecondLoad_ChangesNothing()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await LoadAsync(connection);
        var before = await SnapshotAsync(connection);

        var result = await LoadAsync(connection);

        Assert.Equal((false, 10, 5, 30), result);
        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Fact]
    public async Task OtherData_AreRefused_AndLeftAsTheyAre()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using (var dbContext = database.CreateDbContext(connection))
        {
            dbContext.Notices.Add(NoticeEntity.Create(new Notice("999999-2026", "Inny przetarg", null, null, [], new DateOnly(2026, 11, 2), null, null), Now));
            await dbContext.SaveChangesAsync(Ct);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(connection));

        Assert.Contains("docker compose down --volumes", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["999999-2026"], (await SnapshotAsync(connection)).Split('\n').Where(line => line.StartsWith("notice", StringComparison.Ordinal)).Select(line => line.Split(' ')[1]));
        await using var check = database.CreateDbContext(connection);
        Assert.Equal(0, await check.Documents.CountAsync(Ct));
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("model")]
    [InlineData("vector")]
    public async Task SameKeysWithOneFieldChanged_AreRefused_AndLeftAsTheyAre(string change)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await LoadAsync(connection);
        await using (var dbContext = database.CreateDbContext(connection))
        {
            var notice = await dbContext.Notices.SingleAsync(n => n.PublicationNumber == "100101-2026", Ct);
            var chunk = await dbContext.Documents.Where(d => d.DocId == "syn-01").SelectMany(d => d.Chunks).OrderBy(c => c.Position).FirstAsync(Ct);
            var vector = chunk.Embedding!.ToArray();
            vector[0] = float.BitIncrement(vector[0]);
            switch (change)
            {
                case "deadline": notice.SubmissionDeadline = notice.SubmissionDeadline!.Value.AddDays(1); break;
                case "model": chunk.EmbeddingModel = "other-model"; break;
                default: chunk.Embedding = new Pgvector.Vector(vector); break;
            }

            await dbContext.SaveChangesAsync(Ct);
        }

        var before = await SnapshotAsync(connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(connection));

        Assert.Contains("docker compose down --volumes", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Fact]
    public async Task BrokenPassagesFile_LoadsNothing()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var broken = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(broken, [.. File.ReadLines(Passages).Take(3), """{"doc_id": "syn-01", "page": """], Ct);

            await Assert.ThrowsAnyAsync<Exception>(() => DemoData.LoadAsync(database.CreateDbContext(connection), Notices, broken, Now, Ct));
        }
        finally
        {
            File.Delete(broken);
        }

        Assert.Equal(string.Empty, await SnapshotAsync(connection));
    }

    [Fact]
    public async Task FailedSave_LoadsNothing()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        var duplicated = Path.GetTempFileName();
        try
        {
            // The same notice twice breaks the unique publication number in the middle of the save.
            var lines = File.ReadLines(Notices).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            await File.WriteAllLinesAsync(duplicated, [.. lines, lines[0]], Ct);

            await Assert.ThrowsAsync<DbUpdateException>(() => DemoData.LoadAsync(database.CreateDbContext(connection), duplicated, Passages, Now, Ct));
        }
        finally
        {
            File.Delete(duplicated);
        }

        Assert.Equal(string.Empty, await SnapshotAsync(connection));
    }

    [Fact]
    public async Task TwoLoadsAtOnce_LeaveOneCompleteSet()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();

        var loads = await Task.WhenAll(TryLoadAsync(connection), TryLoadAsync(connection));

        Assert.Contains(true, loads);
        await using var dbContext = database.CreateDbContext(connection);
        Assert.Equal(10, await dbContext.Notices.CountAsync(Ct));
        Assert.Equal(5, await dbContext.Documents.CountAsync(Ct));
        Assert.Equal(30, await dbContext.Documents.SelectMany(d => d.Chunks).CountAsync(Ct));
    }

    private async Task<(bool Loaded, int Notices, int Documents, int Passages)> LoadAsync(string connection)
    {
        await using var dbContext = database.CreateDbContext(connection);
        return await DemoData.LoadAsync(dbContext, Notices, Passages, Now, Ct);
    }

    /// <summary>True when this load stored the data; a load that lost the race may fail on the unique keys.</summary>
    private async Task<bool> TryLoadAsync(string connection)
    {
        try
        {
            return (await LoadAsync(connection)).Loaded;
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Every stored notice and chunk with its key and content, one per line.</summary>
    private async Task<string> SnapshotAsync(string connection)
    {
        await using var dbContext = database.CreateDbContext(connection);
        var notices = (await dbContext.Notices.AsNoTracking().OrderBy(n => n.Id).ToListAsync(Ct))
            .Select(n => $"notice {n.PublicationNumber} {n.SubmissionDeadline} {n.PublicationDate} {n.Title} {n.UpdatedAt}");
        var documents = await dbContext.Documents.AsNoTracking().Include(d => d.Chunks).OrderBy(d => d.Id).ToListAsync(Ct);
        var chunks = documents.SelectMany(d => d.Chunks.OrderBy(c => c.Position)
            .Select(c => $"chunk {d.Id} {d.DocId} {c.Id} {c.PageNumber} {c.Text} {c.EmbeddingModel} {string.Join(',', c.Embedding!.ToArray())}"));
        return string.Join('\n', notices.Concat(chunks));
    }
}
