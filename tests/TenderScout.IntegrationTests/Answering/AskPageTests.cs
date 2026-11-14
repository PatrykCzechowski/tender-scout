using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TenderScout.IntegrationTests.Security;

namespace TenderScout.IntegrationTests.Answering;

/// <summary>
/// The ask page served by the API from wwwroot. The API contract of <c>POST /ask</c>, <c>/health</c> and the
/// OpenAPI document is covered by <see cref="AskEndpointTests"/> on the same host.
/// </summary>
public sealed partial class AskPageTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Root_ServesThePolishQuestionForm_InEveryEnvironment(string environment)
    {
        await using var api = CreateApi(environment);
        using var client = api.CreateClient();

        var response = await client.GetAsync("/", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<html lang=\"pl\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"question\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"number\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryFileThePageAndItsStylesheetsName_IsServedFromThePagesOrigin()
    {
        await using var api = CreateApi("Production");
        using var client = api.CreateClient();
        var html = await client.GetStringAsync("/", Ct);

        // Links to the page's own views ("#skrzynka") are not files.
        var files = Reference().Matches(html).Select(m => m.Groups["path"].Value).Where(p => !p.StartsWith('#')).ToList();
        foreach (var stylesheet in files.Where(f => f.EndsWith(".css", StringComparison.Ordinal)).ToList())
        {
            var css = await client.GetStringAsync("/" + stylesheet, Ct);
            files.AddRange(CssUrl().Matches(css).Select(m => m.Groups["path"].Value));
        }

        Assert.Contains("app.js", files);
        Assert.Contains("fonts/Geist-Variable.woff2", files);
        foreach (var file in files)
        {
            Assert.DoesNotContain(":", file, StringComparison.Ordinal);
            Assert.False(file.StartsWith('/'), $"{file} is not relative to the page");
            using var response = await client.GetAsync("/" + file, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(ContentType(file), response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Theory]
    [InlineData("/missing")]
    [InlineData("/fonts/missing.woff2")]
    [InlineData("/ask/page")]
    public async Task UnknownPaths_AreNotFound_NotThePage(string path)
    {
        await using var api = CreateApi("Production");
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path, Ct)).StatusCode);
    }

    [Fact]
    public async Task PostingToTheRoot_DoesNotReturnThePage()
    {
        await using var api = CreateApi("Production");
        using var client = api.CreateClient();

        var response = await client.PostAsync("/", new StringContent(""), Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_ListsOnlyTheApi_NotThePage()
    {
        await using var api = CreateApi("Production");
        using var client = api.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        Assert.Equal(["/ask", "/agent", "/notices"], document.GetProperty("paths").EnumerateObject().Select(p => p.Name));
        var notices = document.GetProperty("paths").GetProperty("/notices").GetProperty("get");
        Assert.Equal(
            ["query", "cpvPrefix", "deadlineFrom", "deadlineTo", "hasDocuments", "limit"],
            notices.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(["200", "400", "401", "403"], notices.GetProperty("responses").EnumerateObject().Select(r => r.Name));
        Assert.Contains("at most 200", notices.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateApi(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment(environment);
            web.UseSetting("ConnectionStrings:tenderscout", database.ConnectionString);
            web.UseSetting("Embedding:Endpoint", "http://127.0.0.1:59998");
            web.UseSetting("Chat:Endpoint", "http://127.0.0.1:59999");
            web.UseTestIssuer();
        });

    private static string ContentType(string file) => Path.GetExtension(file) switch
    {
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".woff2" => "font/woff2",
        _ => throw new InvalidOperationException($"Unexpected file type: {file}"),
    };

    [GeneratedRegex("""(?:href|src)="(?<path>[^"]+)""")]
    private static partial Regex Reference();

    [GeneratedRegex("""url\('(?<path>[^']+)'\)""")]
    private static partial Regex CssUrl();
}
