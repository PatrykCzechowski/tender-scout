using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using TenderScout.Infrastructure.Tools;
using TenderScout.IntegrationTests.Agent;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Security;

/// <summary>What stays open without a token, and that everything else the two services map is protected.</summary>
public sealed class OpenRoutesTests(PostgresFixture database)
{
    /// <summary>The routes that need no token. A new route that returns data must not be added here.</summary>
    private static readonly string[] Open = ["/health", "/alive", "/openapi/{documentName}.json", "/sign-in-settings"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Api_ServesHealthThePageTheOpenApiDocumentAndTheSignInSettings_WithoutAToken(string environment)
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web
            .UseEnvironment(environment)
            .UseSetting("ConnectionStrings:tenderscout", database.ConnectionString)
            .UseSetting("Authentication:Schemes:Bearer:Authority", "https://issuer.test/realms/example")
            .UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", TestIssuer.Audience)
            .UseSetting("WebSignIn:ClientId", "example-web-client"));
        using var client = api.CreateClient();

        foreach (var path in new[] { "/health", "/alive", "/", "/app.js", "/openapi/v1.json", "/sign-in-settings" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {response.StatusCode}");
        }

        // The settings hold where the issuer is and which client the page is, and nothing else.
        Assert.Equal(
            """{"authority":"https://issuer.test/realms/example","clientId":"example-web-client"}""",
            await client.GetStringAsync("/sign-in-settings", Ct));
    }

    [Fact]
    public async Task McpServer_ServesHealth_WithoutAToken()
    {
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString);
        using var client = mcp.CreateHttpClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive", Ct)).StatusCode);
    }

    [Fact]
    public async Task EveryOtherRouteOfTheTwoServices_Answers401WithoutAToken()
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString);
        using var apiClient = api.CreateAnonymousClient();
        using var mcpClient = mcp.CreateHttpClient();

        var ofApi = await ProtectedAsync(apiClient, api.Routes);
        var ofMcp = await ProtectedAsync(mcpClient, mcp.Routes);

        Assert.Equal(["GET /notices", "POST /agent", "POST /ask"], ofApi.Order());
        Assert.Equal(["POST /mcp/"], ofMcp);
    }

    [Fact]
    public async Task UnknownPath_Is404_NotAChallengeAndNotThePage()
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        await using var mcp = await McpTestHost.StartAsync(database.ConnectionString);
        using var apiClient = api.CreateAnonymousClient();
        using var mcpClient = mcp.CreateHttpClient();

        using var fromApi = await apiClient.GetAsync("/no-such-route", Ct);
        using var fromMcp = await mcpClient.GetAsync("/no-such-route", Ct);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (fromApi.StatusCode, fromMcp.StatusCode));
        Assert.NotEqual("text/html", fromApi.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesTheBearerSchemeAndTheScopeOfEachOperation()
    {
        await using var api = new AgentTestHost(database.ConnectionString);
        using var client = api.CreateAnonymousClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        Assert.Equal(
            """{"type":"http","scheme":"bearer","bearerFormat":"JWT"}""",
            JsonSerializer.Serialize(document.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer")));
        foreach (var (path, method, scope) in new[] { ("/ask", "post", Scopes.DocumentsAsk), ("/agent", "post", Scopes.DocumentsAsk), ("/notices", "get", Scopes.NoticesRead) })
        {
            var operation = document.GetProperty("paths").GetProperty(path).GetProperty(method);
            Assert.Equal($$"""[{"bearer":["{{scope}}"]}]""", JsonSerializer.Serialize(operation.GetProperty("security")));
            Assert.Contains(scope, operation.GetProperty("responses").GetProperty("403").GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        }
    }

    /// <summary>Calls every route outside the open list without a token and returns those that answered 401.</summary>
    private static async Task<List<string>> ProtectedAsync(HttpClient client, IEnumerable<RouteEndpoint> routes)
    {
        var challenged = new List<string>();
        foreach (var route in routes.Where(r => !Open.Contains(r.RoutePattern.RawText) && r.RoutePattern.RawText?.StartsWith("/scalar", StringComparison.Ordinal) != true))
        {
            foreach (var method in route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route.RoutePattern.RawText), Ct);
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {route.RoutePattern.RawText} answered {response.StatusCode} without a token");
                challenged.Add($"{method} {route.RoutePattern.RawText}");
            }
        }

        return challenged;
    }
}
