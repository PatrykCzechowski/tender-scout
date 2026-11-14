using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using TenderScout.Infrastructure.Tools;
using TenderScout.IntegrationTests.Agent;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Security;

/// <summary>
/// Every protected route against every kind of token: 401 without a valid token, 403 with a valid token that
/// lacks the scope, success with the scope. A token that must fail differs from a valid one in one thing only.
/// </summary>
public sealed class TokenMatrixTests(PostgresFixture database)
{
    private static readonly string[] Routes = ["/notices", "/ask", "/agent", "/mcp"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, HttpStatusCode> Cases()
    {
        var cases = new TheoryData<string, string, HttpStatusCode>();
        foreach (var route in Routes)
        {
            foreach (var invalid in new[] { "no token", "not a token", "another key", "another issuer", "another audience", "expired" })
            {
                cases.Add(route, invalid, HttpStatusCode.Unauthorized);
            }

            cases.Add(route, "without the scope", HttpStatusCode.Forbidden);
            cases.Add(route, "with the scope", HttpStatusCode.OK);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProtectedRoute_AnswersByTheTokenItIsGiven(string route, string token, HttpStatusCode expected)
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        api.Chat.Text();
        await using var mcp = route == "/mcp" ? await McpTestHost.StartAsync(database.ConnectionString) : null;
        using var client = mcp?.CreateHttpClient() ?? api.CreateAnonymousClient();
        using var request = Request(route);
        request.Headers.Authorization = Token(route, token) is { } value ? new AuthenticationHeaderValue("Bearer", value) : null;

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Unauthorized)
        {
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        }

        if (expected != HttpStatusCode.OK)
        {
            // Nothing behind the route ran, and the API answers as for its other errors.
            Assert.Empty(api.Chat.Requests);
            Assert.Equal(route == "/mcp" ? null : "application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    /// <summary>The scope the route requires; <c>/mcp</c> accepts either one.</summary>
    private static string Scope(string route) => route == "/notices" ? Scopes.NoticesRead : Scopes.DocumentsAsk;

    private static string? Token(string route, string kind) => kind switch
    {
        "no token" => null,
        "not a token" => "not-a-token",
        "another key" => TestIssuer.Token(Scope(route), key: RandomNumberGenerator.GetBytes(32)),
        "another issuer" => TestIssuer.Token(Scope(route), issuer: "https://another-issuer.test"),
        "another audience" => TestIssuer.Token(Scope(route), audience: "another-api"),
        "expired" => TestIssuer.Token(Scope(route), validFromMinutes: -120, validForMinutes: 60),
        "without the scope" => TestIssuer.Token(route switch
        {
            "/notices" => Scopes.DocumentsAsk,
            "/mcp" => "another:scope",
            _ => Scopes.NoticesRead,
        }),
        _ => TestIssuer.Token(Scope(route)),
    };

    private static HttpRequestMessage Request(string route) => route switch
    {
        "/notices" => new HttpRequestMessage(HttpMethod.Get, route),
        "/mcp" => new HttpRequestMessage(HttpMethod.Post, route)
        {
            Headers = { Accept = { new("application/json"), new("text/event-stream") } },
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } },
            }),
        },
        _ => new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(new { question = AgentTestHost.Question }) },
    };
}
