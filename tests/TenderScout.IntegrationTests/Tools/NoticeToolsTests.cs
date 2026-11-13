using System.Text.Json;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.IntegrationTests.Tools;

/// <summary><c>search_notices</c> and <c>get_notice</c> through an MCP client, on invented notices.</summary>
public sealed class NoticeToolsTests(PostgresFixture database)
{
    private static readonly DateOnly Published = new(2026, 11, 2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Query_NeedsEveryWordInTheTitleOrTheBuyer_AlsoInsideALongerWord()
    {
        await using var mcp = await StartAsync(
            Notice("100001-2026", "Dostawa serwerów", "Urząd Alfa"),
            Notice("100002-2026", "Usługi sprzątania", "Zakład serwerów i sieci"),
            Notice("100003-2026", "Budowa drogi", "Gmina Beta"),
            Notice("100004-2026", "Dostawa mebli", "Gmina Beta"));

        Assert.Equal(["100001-2026", "100002-2026"], await SearchAsync(mcp, new { query = "serwer" }));
        Assert.Equal(["100001-2026"], await SearchAsync(mcp, new { query = "Dostawa Alfa" }));
        Assert.Equal(["100001-2026"], await SearchAsync(mcp, new { query = "  Dostawa\t\nAlfa " }));
        Assert.Empty(await SearchAsync(mcp, new { query = "Dostawa i Alfa" }));
        Assert.Empty(await SearchAsync(mcp, new { query = "Dostawa,Alfa" }));
    }

    [Fact]
    public async Task Query_IgnoresCase_KeepsDiacritics_AndReadsWildcardsAsText()
    {
        await using var mcp = await StartAsync(
            Notice("100001-2026", "WYSOKOŚĆ wadium"),
            Notice("100002-2026", "rabat 10%"),
            Notice("100003-2026", "kod_A"),
            Notice("100004-2026", @"ścieżka C:\dane"),
            Notice("100005-2026", title: null, buyer: null));

        Assert.Equal(["100001-2026"], await SearchAsync(mcp, new { query = "wysokość" }));
        Assert.Empty(await SearchAsync(mcp, new { query = "wysokosc" }));
        Assert.Equal(["100002-2026"], await SearchAsync(mcp, new { query = "%" }));
        Assert.Equal(["100003-2026"], await SearchAsync(mcp, new { query = "_" }));
        Assert.Equal(["100004-2026"], await SearchAsync(mcp, new { query = @"\" }));
    }

    [Fact]
    public async Task CpvPrefix_MatchesTheMainCodeAndTheOtherCodes()
    {
        await using var mcp = await StartAsync(
            Notice("100001-2026", mainCpv: "72000000", cpv: ["72000000"]),
            Notice("100002-2026", mainCpv: "48000000", cpv: ["48000000", "72250000"]),
            Notice("100003-2026", mainCpv: "45000000", cpv: ["45000000"]),
            Notice("100004-2026", mainCpv: "72200000", cpv: ["72200000", "72210000"]),
            Notice("100005-2026"));

        Assert.Equal(["100001-2026", "100002-2026", "100004-2026"], await SearchAsync(mcp, new { cpvPrefix = "72" }));
        Assert.Equal(["100002-2026"], await SearchAsync(mcp, new { cpvPrefix = " 72250000 " }));
    }

    [Fact]
    public async Task DeadlineBounds_AreInclusive_AndLeaveOutNoticesWithoutADeadline()
    {
        await using var mcp = await StartAsync(
            Notice("100001-2026", deadline: new DateOnly(2026, 12, 9)),
            Notice("100002-2026", deadline: new DateOnly(2026, 12, 10)),
            Notice("100003-2026", deadline: new DateOnly(2026, 12, 11)),
            Notice("100004-2026"));

        Assert.Equal(["100002-2026", "100003-2026"], await SearchAsync(mcp, new { deadlineFrom = "2026-12-10" }));
        Assert.Equal(["100001-2026", "100002-2026"], await SearchAsync(mcp, new { deadlineTo = " 2026-12-10 " }));
        Assert.Equal(["100002-2026"], await SearchAsync(mcp, new { deadlineFrom = "2026-12-10", deadlineTo = "2026-12-10" }));
    }

    [Fact]
    public async Task Filters_CombineWithAnd_AndANullFilterIsNotGiven()
    {
        var deadline = new DateOnly(2026, 12, 10);
        await using var mcp = await StartAsync(
            Notice("100001-2026", "Dostawa serwerów", mainCpv: "72000000", deadline: deadline),
            Notice("100002-2026", "Dostawa mebli", mainCpv: "72000000", deadline: deadline),
            Notice("100003-2026", "Dostawa serwerów", mainCpv: "45000000", deadline: deadline),
            Notice("100004-2026", "Dostawa serwerów", mainCpv: "72000000", deadline: deadline.AddDays(-1)));

        var all = new { query = "serwer", cpvPrefix = "72", deadlineFrom = "2026-12-10", deadlineTo = "2026-12-31" };
        Assert.Equal(["100001-2026"], await SearchAsync(mcp, all));
        Assert.Equal(
            ["100001-2026", "100002-2026", "100004-2026"],
            await SearchAsync(mcp, new { query = (string?)null, cpvPrefix = "72", deadlineFrom = (string?)null, deadlineTo = (string?)null }));
    }

    [Fact]
    public async Task Result_IsLimited_NewestFirst_ThenByPublicationNumber()
    {
        // Thirty notices on fifteen days, two per day.
        await using var mcp = await StartAsync([.. Enumerable.Range(1, 30).Select(i =>
            Notice($"{100000 + i}-2026", published: Published.AddDays((i - 1) / 2)))]);
        var newestFirst = Enumerable.Range(1, 30)
            .OrderByDescending(i => (i - 1) / 2).ThenBy(i => i)
            .Select(i => $"{100000 + i}-2026")
            .ToList();

        Assert.Equal(newestFirst.Take(10), await SearchAsync(mcp, new { }));
        Assert.Equal(newestFirst.Take(1), await SearchAsync(mcp, new { limit = 1 }));
        Assert.Equal(newestFirst.Take(25), await SearchAsync(mcp, new { limit = 25 }));
        Assert.Equal(newestFirst.Take(25), await SearchAsync(mcp, new { limit = 25 }));
    }

    [Fact]
    public async Task NoMatch_IsAnEmptyList_AndAListItemHasItsSevenFields()
    {
        await using var mcp = await StartAsync(Notice("100001-2026", "Dostawa serwerów"));

        Assert.Empty(await SearchAsync(mcp, new { query = "nieistniejące" }));
        var result = await mcp.CallAsync("search_notices");
        var item = JsonSerializer.SerializeToElement(result.StructuredContent).GetProperty("notices")[0];
        Assert.Equal(
            ["publicationNumber", "title", "buyerName", "mainCpvCode", "publicationDate", "submissionDeadline", "link"],
            item.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-02", item.GetProperty("publicationDate").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("buyerName").ValueKind);
    }

    [Theory]
    [InlineData("""{"query": "   "}""", "query")]
    [InlineData("""{"cpvPrefix": "7"}""", "cpvPrefix")]
    [InlineData("""{"deadlineFrom": "2026-02-30"}""", "deadlineFrom")]
    [InlineData("""{"deadlineFrom": "2026-12-10", "deadlineTo": "2026-12-09"}""", "deadlineTo")]
    [InlineData("""{"limit": 26}""", "limit")]
    public async Task InvalidInput_IsAnErrorThatNamesTheParameter_AndTheNextCallWorks(string arguments, string parameter)
    {
        await using var mcp = await StartAsync(Notice("100001-2026"));

        var error = await mcp.CallForErrorAsync("search_notices", JsonSerializer.Deserialize<JsonElement>(arguments));

        Assert.Contains(parameter, error, StringComparison.Ordinal);
        Assert.Single(await SearchAsync(mcp, new { }));
    }

    [Fact]
    public async Task GetNotice_ReturnsTheNoticeWithAllItsCpvCodes()
    {
        var stored = new Notice(
            "123456-2026", "Dostawa serwerów", "Urząd Alfa", "72000000", ["72000000", "48820000"],
            Published, new DateOnly(2026, 12, 10), new Uri("https://ted.europa.eu/pl/notice/-/detail/123456-2026"));
        await using var mcp = await StartAsync(stored, Notice("1-2026", "Inne ogłoszenie"));

        var result = await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = " 123456-2026 " });

        Assert.True(result.Found);
        Assert.Null(result.Message);
        Assert.Equal(stored with { CpvCodes = [] }, result.Notice! with { CpvCodes = [] });
        Assert.Equal(stored.CpvCodes, result.Notice.CpvCodes);
        Assert.Equal("Inne ogłoszenie", (await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = "1-2026" })).Notice!.Title);
    }

    [Fact]
    public async Task GetNotice_MissingValuesAreNull_AndCpvCodesAnEmptyList()
    {
        await using var mcp = await StartAsync(Notice("100001-2026", title: null));

        var result = await mcp.CallAsync("get_notice", new { publicationNumber = "100001-2026" });

        var notice = JsonSerializer.SerializeToElement(result.StructuredContent).GetProperty("notice");
        Assert.Equal(
            ["publicationNumber", "title", "buyerName", "mainCpvCode", "cpvCodes", "publicationDate", "submissionDeadline", "link"],
            notice.EnumerateObject().Select(p => p.Name));
        Assert.All(
            new[] { "title", "buyerName", "mainCpvCode", "submissionDeadline", "link" },
            field => Assert.Equal(JsonValueKind.Null, notice.GetProperty(field).ValueKind));
        Assert.Equal(0, notice.GetProperty("cpvCodes").GetArrayLength());
    }

    [Fact]
    public async Task GetNotice_UnknownNumber_IsANormalResultWithFoundFalse()
    {
        await using var mcp = await StartAsync(Notice("100001-2026"));

        var result = await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = "999999-2026" });

        Assert.False(result.Found);
        Assert.Null(result.Notice);
        Assert.Contains("999999-2026", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1234567-2026")]
    [InlineData("123456/2026")]
    [InlineData("")]
    public async Task GetNotice_MalformedNumber_IsAnErrorThatNamesTheParameter(string number)
    {
        await using var mcp = await StartAsync(Notice("100001-2026"));

        var error = await mcp.CallForErrorAsync("get_notice", new { publicationNumber = number });

        Assert.Contains("publicationNumber", error, StringComparison.Ordinal);
        Assert.True((await mcp.CallAsync<NoticeResult>("get_notice", new { publicationNumber = "100001-2026" })).Found);
    }

    private static Notice Notice(
        string number,
        string? title = "Ogłoszenie",
        string? buyer = null,
        string? mainCpv = null,
        string[]? cpv = null,
        DateOnly? published = null,
        DateOnly? deadline = null) =>
        new(number, title, buyer, mainCpv, cpv ?? [], published ?? Published, deadline, null);

    private static async Task<IEnumerable<string>> SearchAsync(McpTestHost mcp, object arguments) =>
        (await mcp.CallAsync<NoticeSearchResult>("search_notices", arguments)).Notices.Select(n => n.PublicationNumber);

    /// <summary>Starts the server on a new database that holds only these notices.</summary>
    private async Task<McpTestHost> StartAsync(params Notice[] notices)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);
        dbContext.Notices.AddRange(notices.Select(n => NoticeEntity.Create(n, DateTimeOffset.UnixEpoch)));
        await dbContext.SaveChangesAsync(Ct);
        return await McpTestHost.StartAsync(connection);
    }
}
