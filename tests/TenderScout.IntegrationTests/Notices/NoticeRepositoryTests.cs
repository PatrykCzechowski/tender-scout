using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Notices;

public sealed class NoticeRepositoryTests(PostgresFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upsert_NewNotice_InsertsRow()
    {
        var notice = CreateNotice();

        var result = await UpsertAsync(notice);

        Assert.Equal(NoticeUpsertResult.Inserted, result);
        var row = await SingleRowAsync(notice.PublicationNumber);
        Assert.Equal(notice.Title, row.Title);
        Assert.Equal(notice.BuyerName, row.BuyerName);
        Assert.Equal(notice.MainCpvCode, row.MainCpvCode);
        Assert.Equal(notice.CpvCodes, row.CpvCodes);
        Assert.Equal(notice.PublicationDate, row.PublicationDate);
        Assert.Equal(notice.SubmissionDeadline, row.SubmissionDeadline);
        Assert.Equal(notice.Link, row.ToNotice().Link);
        Assert.Equal(Now, row.CreatedAt);
        Assert.Equal(Now, row.UpdatedAt);
    }

    [Fact]
    public async Task Upsert_NoticeWithOnlyRequiredFields_RoundTrips()
    {
        var notice = new Notice(UniqueNumber(), null, null, null, [], new DateOnly(2026, 10, 20), null, null);

        await UpsertAsync(notice);

        var stored = (await SingleRowAsync(notice.PublicationNumber)).ToNotice();
        Assert.Equal(notice with { CpvCodes = stored.CpvCodes }, stored);
        Assert.Empty(stored.CpvCodes);
    }

    [Fact]
    public async Task Upsert_ChangedNotice_UpdatesFieldsAndKeepsCreatedAt()
    {
        var original = CreateNotice();
        await UpsertAsync(original);

        _time.Advance(TimeSpan.FromHours(3));
        var changed = original with
        {
            Title = "Zmieniony tytuł",
            CpvCodes = ["72000000", "72200000"],
            SubmissionDeadline = new DateOnly(2026, 12, 1),
        };
        var result = await UpsertAsync(changed);

        Assert.Equal(NoticeUpsertResult.Updated, result);
        var row = await SingleRowAsync(original.PublicationNumber);
        Assert.Equal("Zmieniony tytuł", row.Title);
        Assert.Equal(["72000000", "72200000"], row.CpvCodes);
        Assert.Equal(new DateOnly(2026, 12, 1), row.SubmissionDeadline);
        Assert.Equal(Now, row.CreatedAt);
        Assert.Equal(Now.AddHours(3), row.UpdatedAt);
    }

    [Fact]
    public async Task Upsert_SameNoticeTwice_KeepsOneUnchangedRow()
    {
        var notice = CreateNotice();
        await UpsertAsync(notice);

        _time.Advance(TimeSpan.FromHours(1));
        // A fresh instance with equal content, as a later TED poll would produce.
        var result = await UpsertAsync(notice with { CpvCodes = [.. notice.CpvCodes] });

        Assert.Equal(NoticeUpsertResult.Unchanged, result);
        var row = await SingleRowAsync(notice.PublicationNumber);
        Assert.Equal(Now, row.UpdatedAt);
    }

    [Fact]
    public async Task NoticesTable_RejectsDuplicatePublicationNumber()
    {
        var number = UniqueNumber();
        await using var dbContext = database.CreateDbContext();
        dbContext.Notices.Add(NoticeEntity.Create(CreateNotice(number), Now));
        dbContext.Notices.Add(NoticeEntity.Create(CreateNotice(number), Now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(Ct));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_notices_publication_number", postgres.ConstraintName);
    }

    [Fact]
    public async Task Migrations_CoverTheCurrentModel()
    {
        await using var dbContext = database.CreateDbContext();

        Assert.False(dbContext.Database.HasPendingModelChanges(), "The model has changes without a migration; run `dotnet ef migrations add`.");
        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync(Ct));
    }

    /// <summary>Each upsert runs in its own context, like separate worker runs would.</summary>
    private async Task<NoticeUpsertResult> UpsertAsync(Notice notice)
    {
        await using var dbContext = database.CreateDbContext();
        return await new NoticeRepository(dbContext, _time).UpsertAsync(notice, Ct);
    }

    private async Task<NoticeEntity> SingleRowAsync(string publicationNumber)
    {
        await using var dbContext = database.CreateDbContext();
        return await dbContext.Notices.AsNoTracking().SingleAsync(n => n.PublicationNumber == publicationNumber, Ct);
    }

    /// <summary>Tests share one database, so each uses its own publication numbers.</summary>
    private static string UniqueNumber() => $"{Random.Shared.Next(100_000, 999_999)}-{Guid.NewGuid():N}"[..32];

    private static Notice CreateNotice(string? publicationNumber = null) =>
        new(
            PublicationNumber: publicationNumber ?? UniqueNumber(),
            Title: "Polska – Usługi informatyczne – Utrzymanie systemu SIOEZ",
            BuyerName: "Centralna Komisja Egzaminacyjna",
            MainCpvCode: "72000000",
            CpvCodes: ["72000000", "72100000"],
            PublicationDate: new DateOnly(2026, 10, 20),
            SubmissionDeadline: new DateOnly(2026, 11, 3),
            Link: new Uri("https://ted.europa.eu/pl/notice/-/detail/722762-2026"));
}
