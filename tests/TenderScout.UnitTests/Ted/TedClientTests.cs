using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenderScout.Infrastructure.Ted;

namespace TenderScout.UnitTests.Ted;

public sealed class TedClientTests
{
    private static readonly DateOnly Day = new(2026, 10, 20);

    [Fact]
    public async Task SearchNoticesAsync_FollowsIterationTokensUntilLastPage()
    {
        var handler = new FakeTedHandler()
            .RespondWithFixture("search-page-1.json")
            .RespondWithFixture("search-page-2.json")
            .RespondWithFixture("search-page-3.json");
        using var provider = TedClientFactory.Create(handler);

        var notices = await Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["722731-2026", "722762-2026", "722800-2026", "723696-2026", "722966-2026"],
            notices.Select(notice => notice.PublicationNumber));

        // Page 3 still carries a token, but it is short, so no fourth request is made.
        Assert.Equal(3, handler.Requests.Count);
        var bodies = handler.Requests.Select(request => JsonDocument.Parse(request.Body!).RootElement).ToArray();
        Assert.False(bodies[0].TryGetProperty("iterationNextToken", out _));
        Assert.Equal(TokenOf("search-page-1.json"), bodies[1].GetProperty("iterationNextToken").GetString());
        Assert.Equal(TokenOf("search-page-2.json"), bodies[2].GetProperty("iterationNextToken").GetString());
    }

    [Fact]
    public async Task SearchNoticesAsync_SendsSearchRequestForPolishItTenders()
    {
        var handler = new FakeTedHandler().RespondWithFixture("search-missing-fields.json");
        using var provider = TedClientFactory.Create(handler, new() { ["Ted:PageSize"] = "100" });

        await Client(provider).SearchNoticesAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://ted.test/api/v3/notices/search"), request.Uri);

        var body = JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal(
            "buyer-country=POL AND (main-classification-proc=72* OR main-classification-proc=48*) "
            + "AND publication-date>=20261001 AND publication-date<=20261031",
            body.GetProperty("query").GetString());
        Assert.Equal(100, body.GetProperty("limit").GetInt32());
        Assert.Equal("ITERATION", body.GetProperty("paginationMode").GetString());
        Assert.Equal("ALL", body.GetProperty("scope").GetString());
        Assert.Contains("publication-number", body.GetProperty("fields").EnumerateArray().Select(field => field.GetString()));
    }

    [Fact]
    public async Task SearchNoticesAsync_MapsRecordedNotice()
    {
        var handler = new FakeTedHandler()
            .RespondWithFixture("search-page-1.json")
            .RespondWithFixture("search-page-2.json")
            .RespondWithFixture("search-page-3.json");
        using var provider = TedClientFactory.Create(handler);

        var notices = await Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken);

        var notice = notices[0];
        Assert.Equal("722731-2026", notice.PublicationNumber);
        Assert.Equal(
            "Polska – Pakiety oprogramowania i systemy informatyczne – System wykrywania anomalii w ruchu SMTP w oparciu o Self-Learning AI",
            notice.Title);
        Assert.Equal("Narodowy Bank Polski", notice.BuyerName);
        Assert.Equal("48000000", notice.MainCpvCode);
        Assert.Equal(["48000000", "72250000", "72263000", "72268000"], notice.CpvCodes);
        Assert.Equal(new DateOnly(2026, 10, 20), notice.PublicationDate);
        Assert.Equal(new DateOnly(2026, 11, 19), notice.SubmissionDeadline);
        Assert.Equal(new Uri("https://ted.europa.eu/pl/notice/-/detail/722731-2026"), notice.Link);

        // Award-type notice without a submission deadline.
        Assert.Null(notices.Single(n => n.PublicationNumber == "723696-2026").SubmissionDeadline);
    }

    [Fact]
    public async Task SearchNoticesAsync_MissingOptionalFields_MapToNullsWithoutThrowing()
    {
        var handler = new FakeTedHandler().RespondWithFixture("search-missing-fields.json");
        using var provider = TedClientFactory.Create(handler, new() { ["Ted:PageSize"] = "100" });

        var notices = await Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken);

        var sparse = notices[0];
        Assert.Equal("723810-2026", sparse.PublicationNumber);
        Assert.Equal(Day, sparse.PublicationDate);
        Assert.Null(sparse.Title);
        Assert.Null(sparse.BuyerName);
        Assert.Null(sparse.MainCpvCode);
        Assert.Empty(sparse.CpvCodes);
        Assert.Null(sparse.SubmissionDeadline);
        Assert.Null(sparse.Link);

        var partial = notices[1];
        Assert.Equal("Poland – System and support services – USŁUGA UTRZYMANIA I ROZWOJU GDAŃSKIEJ PLATFORMY EDUKACYJNEJ", partial.Title);
        Assert.Null(partial.BuyerName);
        Assert.Null(partial.MainCpvCode);
        Assert.Empty(partial.CpvCodes);
        Assert.Null(partial.Link);
        Assert.Equal(new DateOnly(2026, 10, 23), partial.SubmissionDeadline);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task SearchNoticesAsync_RetriesTransientStatus(HttpStatusCode status)
    {
        var handler = new FakeTedHandler()
            .RespondWithStatus(status)
            .RespondWithStatus(status)
            .RespondWithFixture("search-missing-fields.json");
        using var provider = TedClientFactory.Create(handler, new() { ["Ted:PageSize"] = "100" });

        var notices = await Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken);

        Assert.Equal(2, notices.Count);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchNoticesAsync_RetriesAttemptThatTimesOut()
    {
        var handler = new FakeTedHandler()
            .Hang()
            .RespondWithFixture("search-missing-fields.json");
        using var provider = TedClientFactory.Create(handler, new()
        {
            ["Ted:PageSize"] = "100",
            ["Ted:AttemptTimeout"] = "00:00:00.200",
        });

        var notices = await Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken);

        Assert.Equal(2, notices.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchNoticesAsync_PersistentServerError_ThrowsAfterAllRetries()
    {
        var handler = new FakeTedHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.RespondWithStatus(HttpStatusCode.BadGateway);
        }

        using var provider = TedClientFactory.Create(handler, new() { ["Ted:MaxRetryAttempts"] = "2" });

        var exception = await Assert.ThrowsAsync<TedApiException>(
            () => Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchNoticesAsync_WithServiceDefaults_RunsOnlyTheTedRetryPipeline()
    {
        var handler = new FakeTedHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.RespondWithStatus(HttpStatusCode.BadGateway);
        }

        using var provider = TedClientFactory.Create(handler, new() { ["Ted:MaxRetryAttempts"] = "2" }, withServiceDefaults: true);

        await Assert.ThrowsAsync<TedApiException>(
            () => Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken));

        // Two stacked pipelines would multiply the attempts (and hit the unscripted 4th request).
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchNoticesAsync_PersistentTimeout_ThrowsTedApiException()
    {
        var handler = new FakeTedHandler().Hang().Hang();
        using var provider = TedClientFactory.Create(handler, new()
        {
            ["Ted:MaxRetryAttempts"] = "1",
            ["Ted:AttemptTimeout"] = "00:00:00.100",
        });

        var exception = await Assert.ThrowsAsync<TedApiException>(
            () => Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken));

        Assert.IsType<Polly.ExecutionRejectedException>(exception.InnerException, exactMatch: false);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchNoticesAsync_BadRequest_ThrowsWithTedErrorAndDoesNotRetry()
    {
        var handler = new FakeTedHandler().RespondWithFixture("error-unknown-field.json", HttpStatusCode.BadRequest);
        using var provider = TedClientFactory.Create(handler);

        var exception = await Assert.ThrowsAsync<TedApiException>(
            () => Client(provider).SearchNoticesAsync(Day, Day, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("QUERY_UNKNOWN_FIELD", exception.ErrorType);
        Assert.Contains("Unknown search field 'bogus'", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchNoticesAsync_EndBeforeStart_Throws()
    {
        using var provider = TedClientFactory.Create(new FakeTedHandler());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Client(provider).SearchNoticesAsync(Day, Day.AddDays(-1), TestContext.Current.CancellationToken));
    }

    private static ITedClient Client(IServiceProvider provider) => provider.GetRequiredService<ITedClient>();

    private static string TokenOf(string fixture) =>
        JsonDocument.Parse(Fixtures.Read(fixture)).RootElement.GetProperty("iterationNextToken").GetString()!;

}
