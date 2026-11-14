using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TenderScout.Domain.Documents;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;
using TenderScout.IntegrationTests.Tools;
using TenderScout.IntegrationTests.Security;

namespace TenderScout.IntegrationTests.Notices;

/// <summary><c>GET /notices</c> through HTTP on the API in memory, with invented notices and documents.</summary>
public sealed class NoticesEndpointTests(PostgresFixture database)
{
    private static readonly DateOnly Published = new(2026, 11, 2);
    private static readonly DateOnly Deadline = new(2026, 12, 10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Notice_HasItsNineFields_AndMissingValuesAreNull()
    {
        var full = new Notice(
            "123456-2026", "Dostawa serwerów", "Urząd Alfa", "72000000", ["72000000", "48820000"],
            Published, Deadline, new Uri("https://ted.europa.eu/pl/notice/-/detail/123456-2026"));
        var connection = await SeedAsync([full, Notice("100001-2026", title: null)], ["123456-2026"]);

        var notices = (await GetAsync(connection, "")).GetProperty("notices");

        var first = notices[0];
        Assert.Equal(
            ["publicationNumber", "title", "buyerName", "mainCpvCode", "cpvCodes", "publicationDate", "submissionDeadline", "link", "hasDocuments"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["72000000", "48820000"], first.GetProperty("cpvCodes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("2026-12-10", first.GetProperty("submissionDeadline").GetString());
        Assert.Equal("https://ted.europa.eu/pl/notice/-/detail/123456-2026", first.GetProperty("link").GetString());
        Assert.True(first.GetProperty("hasDocuments").GetBoolean());

        var empty = notices[1];
        Assert.All(
            new[] { "title", "buyerName", "mainCpvCode", "submissionDeadline", "link" },
            field => Assert.Equal(JsonValueKind.Null, empty.GetProperty(field).ValueKind));
        Assert.Equal(0, empty.GetProperty("cpvCodes").GetArrayLength());
        Assert.False(empty.GetProperty("hasDocuments").GetBoolean());
    }

    [Fact]
    public async Task Filters_FindTheSameNoticesAsTheSearchTool()
    {
        var connection = await SeedAsync(
            [
                Notice("100001-2026", "Dostawa serwerów", "Urząd Alfa", "72000000", deadline: Deadline),
                Notice("100002-2026", "Usługi sprzątania", "Zakład serwerów", "90900000", ["90900000", "72250000"], Deadline.AddDays(1)),
                Notice("100003-2026", "Dostawa mebli", "Gmina Beta", "39100000", deadline: Deadline.AddDays(-1)),
                Notice("100004-2026", "Oprogramowanie biurowe", "Gmina Beta", "48000000", deadline: Deadline.AddDays(30)),
                Notice("100005-2026", "Serwis serwerów", "Urząd Alfa", "72000000"),
            ],
            []);
        object[] filters =
        [
            new { query = "serwer" },
            new { query = " Dostawa  Alfa " },
            new { cpvPrefix = "72" },
            new { cpvPrefix = " 72250000 " },
            new { deadlineFrom = "2026-12-10" },
            new { deadlineTo = " 2026-12-10 " },
            new { deadlineFrom = "2026-12-10", deadlineTo = "2026-12-11" },
            new { query = "serwer", cpvPrefix = "72", deadlineFrom = "2026-12-01", deadlineTo = "2026-12-31" },
        ];
        await using var mcp = await McpTestHost.StartAsync(connection);

        foreach (var filter in filters)
        {
            var fromTool = (await mcp.CallAsync<NoticeSearchResult>("search_notices", filter)).Notices.Select(n => n.PublicationNumber);
            var fromEndpoint = Numbers(await GetAsync(connection, QueryString(filter)));
            Assert.Equal(fromTool.Order(), fromEndpoint.Order());
        }
    }

    [Fact]
    public async Task Total_CountsEveryMatch_WhateverTheLimit()
    {
        var connection = await SeedAsync([.. Enumerable.Range(1, 60).Select(i => Notice($"{100000 + i}-2026", deadline: Deadline))], []);

        var byDefault = await GetAsync(connection, "");
        var one = await GetAsync(connection, "?limit=1");
        var all = await GetAsync(connection, "?limit=200");

        Assert.Equal((60, 50), (byDefault.GetProperty("total").GetInt32(), byDefault.GetProperty("notices").GetArrayLength()));
        Assert.Equal((60, 1), (one.GetProperty("total").GetInt32(), one.GetProperty("notices").GetArrayLength()));
        Assert.Equal(60, all.GetProperty("notices").GetArrayLength());
        Assert.Equal((0, 0), await CountsAsync(connection, "?query=nieistniejące"));
    }

    [Fact]
    public async Task Order_IsSoonestDeadlineFirst_ThenPublicationNumber_WithoutADeadlineLast()
    {
        var connection = await SeedAsync(
            [
                Notice("100001-2026"),
                Notice("100002-2026", deadline: Deadline.AddDays(5), published: Published.AddDays(9)),
                Notice("100004-2026", deadline: Deadline),
                Notice("100003-2026", deadline: Deadline),
                Notice("100005-2026", deadline: Deadline.AddDays(-3), published: Published.AddDays(-9)),
            ],
            []);

        Assert.Equal(
            ["100005-2026", "100003-2026", "100004-2026", "100002-2026", "100001-2026"],
            Numbers(await GetAsync(connection, "")));
    }

    [Fact]
    public async Task HasDocuments_FlagsAndFiltersNotices_AndOnlyReads()
    {
        var connection = await SeedAsync(
            [Notice("100001-2026", deadline: Deadline), Notice("100002-2026", deadline: Deadline)],
            ["100001-2026", "100001-2026", "999999-2026"]);

        var all = await GetAsync(connection, "");
        Assert.Equal([true, false], all.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("hasDocuments").GetBoolean()));
        Assert.Equal(["100001-2026"], Numbers(await GetAsync(connection, "?hasDocuments=true")));
        Assert.Equal(["100001-2026"], Numbers(await GetAsync(connection, "?hasDocuments=TRUE")));
        Assert.Equal(["100002-2026"], Numbers(await GetAsync(connection, "?hasDocuments=False")));

        await using var dbContext = database.CreateDbContext(connection);
        Assert.Equal(2, await dbContext.Notices.CountAsync(Ct));
        Assert.Equal(3, await dbContext.Documents.CountAsync(Ct));
        Assert.All(await dbContext.Notices.ToListAsync(Ct), n => Assert.Equal(DateTimeOffset.UnixEpoch, n.UpdatedAt));
    }

    [Theory]
    [InlineData("?query=", "query")]
    [InlineData("?query=%20%20%20", "query")]
    [InlineData("?cpvPrefix=7", "cpvPrefix")]
    [InlineData("?cpvPrefix=72a", "cpvPrefix")]
    [InlineData("?deadlineFrom=2026-02-30", "deadlineFrom")]
    [InlineData("?deadlineTo=10.12.2026", "deadlineTo")]
    [InlineData("?deadlineFrom=2026-12-10&deadlineTo=2026-12-09", "deadlineTo")]
    [InlineData("?hasDocuments=maybe", "hasDocuments")]
    [InlineData("?hasDocuments=", "hasDocuments")]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=201", "limit")]
    [InlineData("?limit=abc", "limit")]
    [InlineData("?limit=", "limit")]
    public async Task InvalidParameter_Is400ProblemDetailsNamingIt_InEveryEnvironment_WithoutQuerying(string queryString, string parameter)
    {
        foreach (var environment in new[] { "Development", "Production" })
        {
            var problem = await GetProblemAsync(queryString, environment);

            Assert.Equal([parameter], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));
        }
    }

    [Fact]
    public async Task SeveralInvalidParameters_AreReportedTogether()
    {
        var problem = await GetProblemAsync($"?query={new string('a', 201)}&cpvPrefix=7&limit=abc&hasDocuments=yes", "Production");

        Assert.Equal(
            ["cpvPrefix", "hasDocuments", "limit", "query"],
            problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order());
    }

    private static Notice Notice(
        string number,
        string? title = "Ogłoszenie",
        string? buyer = null,
        string? mainCpv = null,
        string[]? cpv = null,
        DateOnly? deadline = null,
        DateOnly? published = null) =>
        new(number, title, buyer, mainCpv, cpv ?? (mainCpv is null ? [] : [mainCpv]), published ?? Published, deadline, null);

    private static IEnumerable<string> Numbers(JsonElement response) =>
        response.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("publicationNumber").GetString()!);

    private static string QueryString(object filter) =>
        "?" + string.Join('&', filter.GetType().GetProperties().Select(p => $"{p.Name}={Uri.EscapeDataString((string)p.GetValue(filter)!)}"));

    private static WebApplicationFactory<Program> CreateApi(string connection, string environment = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment(environment);
            web.UseSetting("ConnectionStrings:tenderscout", connection);
            web.UseSetting("Embedding:Endpoint", "http://127.0.0.1:59998");
            web.UseSetting("Chat:Endpoint", "http://127.0.0.1:59999");
            web.UseTestIssuer();
        });

    private static async Task<JsonElement> GetAsync(string connection, string queryString)
    {
        await using var api = CreateApi(connection);
        using var client = api.CreateClient().WithToken();
        using var response = await client.GetAsync("/notices" + queryString, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<(int Total, int Count)> CountsAsync(string connection, string queryString)
    {
        var response = await GetAsync(connection, queryString);
        return (response.GetProperty("total").GetInt32(), response.GetProperty("notices").GetArrayLength());
    }

    /// <summary>The API points at a port where no database listens, so a query would end in 500, not 400.</summary>
    private static async Task<JsonElement> GetProblemAsync(string queryString, string environment)
    {
        await using var api = CreateApi("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1", environment);
        using var client = api.CreateClient().WithToken();
        using var response = await client.GetAsync("/notices" + queryString, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>A new database with these notices and one document for each given publication number.</summary>
    private async Task<string> SeedAsync(Notice[] notices, string[] documentTenders)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);
        dbContext.Notices.AddRange(notices.Select(n => NoticeEntity.Create(n, DateTimeOffset.UnixEpoch)));
        dbContext.Documents.AddRange(documentTenders.Select((number, i) => DocumentEntity.Create(
            new TenderDocument($"swz-{i:000}", number, "swz", $"{number}/swz-{i}.pdf", new string('0', 64), null),
            DateTimeOffset.UnixEpoch)));
        await dbContext.SaveChangesAsync(Ct);
        return connection;
    }
}
