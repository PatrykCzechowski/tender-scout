using System.Net;
using System.Net.Http.Json;
using System.Text;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Tools;
using TenderScout.IntegrationTests.Agent;

namespace TenderScout.IntegrationTests.Security;

/// <summary>Which scope opens which endpoint of the API, and how a token and its scopes are read.</summary>
public sealed class ScopeTests(PostgresFixture database)
{
    private static readonly object Question = new { question = AgentTestHost.Question };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Scopes.NoticesRead, HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(Scopes.DocumentsAsk, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData(TestIssuer.BothScopes, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK)]
    public async Task Scope_OpensItsOwnEndpointsOnly(string scope, HttpStatusCode notices, HttpStatusCode ask, HttpStatusCode agent)
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        api.Chat.Text();
        using var client = api.CreateClient(TestIssuer.Token(scope));

        using var fromNotices = await client.GetAsync("/notices", Ct);
        using var fromAsk = await client.PostAsJsonAsync("/ask", Question, Ct);
        using var fromAgent = await client.PostAsJsonAsync("/agent", Question, Ct);

        Assert.Equal((notices, ask, agent), (fromNotices.StatusCode, fromAsk.StatusCode, fromAgent.StatusCode));
    }

    [Theory]
    [InlineData("", HttpStatusCode.Forbidden)]
    [InlineData("openid profile", HttpStatusCode.Forbidden)]
    [InlineData("notices:reader", HttpStatusCode.Forbidden)]
    [InlineData("Notices:Read", HttpStatusCode.Forbidden)]
    [InlineData("all-notices:read", HttpStatusCode.Forbidden)]
    [InlineData("notices:read", HttpStatusCode.OK)]
    [InlineData("openid notices:read profile", HttpStatusCode.OK)]
    public async Task Scope_CountsOnlyAsAWholeEntryOfTheScopeClaim(string scope, HttpStatusCode expected)
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        using var client = api.CreateClient(TestIssuer.Token(scope));

        using var response = await client.GetAsync("/notices", Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/ask", "{}")]
    [InlineData("POST", "/agent", "this is not JSON")]
    [InlineData("GET", "/notices?limit=many", null)]
    public async Task Token_IsCheckedBeforeTheRequestIsRead(string method, string path, string? body)
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        var needed = path.StartsWith("/notices", StringComparison.Ordinal) ? Scopes.NoticesRead : Scopes.DocumentsAsk;
        var other = needed == Scopes.NoticesRead ? Scopes.DocumentsAsk : Scopes.NoticesRead;

        async Task<HttpStatusCode> SendAsync(HttpClient client)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        using var anonymous = api.CreateAnonymousClient();
        using var withoutTheScope = api.CreateClient(TestIssuer.Token(other));
        using var withTheScope = api.CreateClient(TestIssuer.Token(needed));

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(anonymous));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(withoutTheScope));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(withTheScope));
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("not yet valid")]
    [InlineData("in the address")]
    [InlineData("in a cookie")]
    [InlineData("another scheme")]
    public async Task Token_IsAcceptedOnlyAsAValidSignedBearerHeader(string kind)
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        using var client = api.CreateAnonymousClient();
        var valid = TestIssuer.Token();
        using var request = new HttpRequestMessage(HttpMethod.Get, kind == "in the address" ? $"/notices?access_token={valid}" : "/notices");
        _ = kind switch
        {
            "unsigned" => request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {TestIssuer.Token(signed: false)}"),
            "not yet valid" => request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {TestIssuer.Token(validFromMinutes: 60)}"),
            "in a cookie" => request.Headers.TryAddWithoutValidation("Cookie", $"access_token={valid}"),
            "another scheme" => request.Headers.TryAddWithoutValidation("Authorization", $"Token {valid}"),
            _ => false,
        };

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task AgentReadsNoticesWithItsOwnTools_WhenTheCallerHasOnlyTheDocumentsScope()
    {
        Notice notice = new("940111-2026", "Dostawa ambulansów", null, null, [], new DateOnly(2026, 11, 3), null, null);
        await using var api = new AgentTestHost(await AgentTestHost.SeedAsync(database, [notice]));
        api.Chat.Calls(("search_notices", new { query = "ambulans" })).Text().Answer("Jest takie ogłoszenie.", 1);
        using var client = api.CreateClient(TestIssuer.Token(Scopes.DocumentsAsk));

        using var fromAgent = await client.PostAsJsonAsync("/agent", Question, Ct);
        using var fromNotices = await client.GetAsync("/notices", Ct);

        var answer = (await fromAgent.Content.ReadFromJsonAsync<TenderScout.Infrastructure.Agent.AgentResponse>(Ct))!;
        Assert.Equal(("search_notices", false), Assert.Single(answer.ToolCalls.Select(call => (call.Name, call.Failed))));
        Assert.Equal("940111-2026", Assert.Single(answer.Notices).PublicationNumber);
        Assert.Equal(HttpStatusCode.Forbidden, fromNotices.StatusCode);
    }
}
