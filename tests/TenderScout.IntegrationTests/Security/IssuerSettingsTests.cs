extern alias McpHost;

using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TenderScout.IntegrationTests.Security;

/// <summary>The issuer and the audience a service trusts come from its settings and from nothing else.</summary>
public sealed class IssuerSettingsTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachService_AcceptsTheTokensOfItsOwnIssuerAndAudience_AndRefusesThoseOfTheOther()
    {
        // The same signing key on both sides, so only the issuer and the audience tell the tokens apart.
        await using var first = Api(web => web.UseTestIssuer("https://first.test", "first-api"));
        await using var second = Api(web => web.UseTestIssuer("https://second.test", "second-api"));
        var forFirst = TestIssuer.Token(issuer: "https://first.test", audience: "first-api");
        var forSecond = TestIssuer.Token(issuer: "https://second.test", audience: "second-api");

        async Task<HttpStatusCode> NoticesAsync(WebApplicationFactory<Program> api, string token)
        {
            using var client = api.CreateClient().WithToken(token);
            using var response = await client.GetAsync("/notices", Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await NoticesAsync(first, forFirst));
        Assert.Equal(HttpStatusCode.OK, await NoticesAsync(second, forSecond));
        Assert.Equal(HttpStatusCode.Unauthorized, await NoticesAsync(first, forSecond));
        Assert.Equal(HttpStatusCode.Unauthorized, await NoticesAsync(second, forFirst));
        Assert.Equal(HttpStatusCode.Unauthorized, await NoticesAsync(first, TestIssuer.Token(issuer: "https://first.test", audience: "second-api")));
        Assert.Equal(HttpStatusCode.Unauthorized, await NoticesAsync(first, TestIssuer.Token(issuer: "https://second.test", audience: "first-api")));
    }

    [Theory]
    [InlineData(null, "tender-scout-test", "Authentication:Schemes:Bearer:Authority")]
    [InlineData("  ", "tender-scout-test", "Authentication:Schemes:Bearer:Authority")]
    [InlineData("https://issuer.test", null, "Authentication:Schemes:Bearer:ValidAudiences:0")]
    [InlineData("https://issuer.test", "  ", "Authentication:Schemes:Bearer:ValidAudiences:0")]
    public async Task ServiceWithoutAnIssuerOrAnAudience_DoesNotStart_AndNamesTheSetting(string? authority, string? audience, string named)
    {
        void Configure(IWebHostBuilder web) => web
            .UseSetting("ConnectionStrings:tenderscout", database.ConnectionString)
            .UseSetting("Authentication:Schemes:Bearer:Authority", authority)
            .UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", audience);
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(Configure);
        await using var mcp = new WebApplicationFactory<McpHost::Program>().WithWebHostBuilder(Configure);

        var ofApi = Assert.Throws<InvalidOperationException>(() => api.CreateClient());
        var ofMcp = Assert.Throws<InvalidOperationException>(() => mcp.CreateClient());

        Assert.Contains(named, ofApi.Message, StringComparison.Ordinal);
        Assert.Contains(named, ofMcp.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssuerThatCannotBeReached_NeverMeansAccess()
    {
        await using var api = Api(web => web
            .UseSetting("Authentication:Schemes:Bearer:Authority", "http://127.0.0.1:59997/realms/none")
            .UseSetting("Authentication:Schemes:Bearer:RequireHttpsMetadata", "false")
            .UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", TestIssuer.Audience));
        using var client = api.CreateClient().WithToken(TestIssuer.Token(issuer: "http://127.0.0.1:59997/realms/none", key: RandomNumberGenerator.GetBytes(32)));

        using var notices = await client.GetAsync("/notices", Ct);
        using var health = await client.GetAsync("/health", Ct);

        Assert.False(notices.IsSuccessStatusCode, $"/notices answered {notices.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private WebApplicationFactory<Program> Api(Action<IWebHostBuilder> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:tenderscout", database.ConnectionString);
            configure(web);
        });
}
