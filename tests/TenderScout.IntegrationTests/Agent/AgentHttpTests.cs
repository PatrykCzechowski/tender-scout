using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Polly.Timeout;
using TenderScout.Domain.Notices;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>What <c>POST /agent</c> answers when it is not 200, and that a failed request leaves the API working.</summary>
public sealed class AgentHttpTests(PostgresFixture database)
{
    private static readonly Notice Servers = new("111111-2026", "Dostawa serwerów", null, null, [], new DateOnly(2026, 11, 2), null, null);

    private static readonly DocumentToolTests.Chunk Deposit = new("111111-2026", "swz-a", 3, "Wadium wynosi 10 000 zł.", DocumentToolTests.Vector(1, 0));

    private static readonly (string Tool, object Arguments) GetServers = ("get_notice", new { publicationNumber = "111111-2026" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"question": null}""")]
    [InlineData("""{"question": ""}""")]
    [InlineData("""{"question": "   "}""")]
    [InlineData("long")]
    public async Task InvalidQuestion_Is400NamingTheField_WithoutCallingAModel(string body)
    {
        await using var agent = new AgentTestHost(database.ConnectionString);
        using var client = agent.CreateClient();
        var json = body == "long" ? JsonSerializer.Serialize(new { question = new string('a', 1001) }) : body;

        var response = await client.PostAsync("/agent", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(problem.GetProperty("errors").TryGetProperty("question", out _));
        Assert.Empty(agent.Chat.Requests);
    }

    [Fact]
    public async Task QuestionOf1And1000Characters_ReachesTheModelTrimmed()
    {
        await using var agent = new AgentTestHost(database.ConnectionString);
        agent.Chat.Text().Text();

        await agent.AskAsync("  a  ");
        await agent.AskAsync($" {new string('a', 1000)} ");

        Assert.Equal(["a", new string('a', 1000)], agent.Chat.Requests.Select(r => r.Messages[1].Text));
    }

    [Theory]
    [InlineData("gathering", "connection")]
    [InlineData("gathering", "no tool support")]
    [InlineData("gathering", "pipeline timeout")]
    [InlineData("answering", "model not pulled")]
    [InlineData("answering", "client timeout")]
    public async Task ChatModelDown_Is503WithoutDetails_AndIsNotTriedAgain(string phase, string failure)
    {
        Exception exception = failure switch
        {
            "connection" => new HttpRequestException("Connection refused (http://192.0.2.99:11434/api/chat)"),
            "no tool support" => new HttpRequestException("Response status code does not indicate success: 400 (Bad Request).", null, HttpStatusCode.BadRequest),
            "model not pulled" => new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound),
            "client timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 90 seconds elapsing."),
            _ => new TimeoutRejectedException("The operation didn't complete within the allowed timeout."),
        };
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        _ = phase == "gathering" ? agent.Chat.Fails(exception) : agent.Chat.Calls(GetServers).Text().Fails(exception);

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("192.0.2.99", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at TenderScout.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentTestHost.AgentModel, body, StringComparison.Ordinal);
        Assert.DoesNotContain("toolCalls", body, StringComparison.Ordinal);
        Assert.Equal(phase == "gathering" ? 1 : 3, agent.Chat.Requests.Count);
        Assert.Single(agent.AgentLog, entry => entry.Contains("Agent run unavailable:", StringComparison.Ordinal));
        Assert.Single(agent.AgentLog, entry => entry.StartsWith("Warning:", StringComparison.Ordinal)
            && entry.Contains(AgentTestHost.AgentModel, StringComparison.Ordinal)
            && entry.Contains(exception.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmbeddingModelDownDuringADocumentSearch_Is503_NotAnAnswerFromTheNoticesAlone()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers], Deposit));
        agent.Generator.FailOnCall = 1;
        agent.Chat.Calls(GetServers, ("search_tender_documents", new { query = "wadium" }), GetServers);

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("simulated", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Single(agent.Chat.Requests);
        Assert.Single(agent.AgentLog, entry => entry.Contains("Agent run unavailable: 1 round trips, 2 tool calls", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAnAgentModel_TheApiStartsAndAskWorks_ButAgentIs503WithoutCallingAModel(string? agentModel)
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [], Deposit), agentModel);
        agent.Chat.Answer("Wadium wynosi 10 000 zł.", 1);
        using var client = agent.CreateClient();

        var health = await client.GetAsync("/health", Ct);
        var ask = await client.PostAsJsonAsync("/ask", new { question = "Jaka jest wysokość wadium?" }, Ct);
        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.False((await ask.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refused").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Single(agent.Chat.Requests);
    }

    [Fact]
    public async Task Ask_KeepsItsOwnModel_WhenTheAgentHasAnother()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [], Deposit));
        agent.Chat.Answer("Wadium wynosi 10 000 zł.", 1);
        using var client = agent.CreateClient();

        await client.PostAsJsonAsync("/ask", new { question = "Jaka jest wysokość wadium?" }, Ct);

        Assert.Null(Assert.Single(agent.Chat.Requests).Options!.ModelId);
    }

    [Fact]
    public async Task CallerWhoLeaves_StopsTheRunWithoutAnError_AndTheNextRequestWorks()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        agent.Chat.Calls(GetServers).Waits().Text();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var asking = agent.PostAsync(new { question = AgentTestHost.Question }, caller.Token);
        await agent.Chat.Waiting.Task.WaitAsync(Ct);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => asking);
        while (!agent.AgentLog.Any(entry => entry.Contains("Agent run", StringComparison.Ordinal)))
        {
            await Task.Delay(20, Ct);
        }

        var next = await agent.AskAsync();

        Assert.Empty(agent.Errors);
        Assert.StartsWith("Information: Agent run cancelled: 2 round trips, 1 tool calls", agent.AgentLog.Single(entry => entry.Contains("Agent run cancelled", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.True(next.Refused);
        Assert.Empty(next.ToolCalls);
    }

    [Fact]
    public async Task UnexpectedFailureOutsideATool_Is500WithoutDetails_AndTheNextRequestWorks()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        agent.Chat.Calls(GetServers).Fails(new InvalidOperationException("internal-detail-marker")).Text();

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });
        var next = await agent.AskAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("internal-detail-marker", body, StringComparison.Ordinal);
        Assert.DoesNotContain("toolCalls", body, StringComparison.Ordinal);
        Assert.Single(agent.AgentLog, entry => entry.StartsWith("Information: Agent run failed: 2 round trips, 1 tool calls", StringComparison.Ordinal));
        Assert.True(next.Refused);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesAgentWithItsRequestBodyAndResponses_BesideAsk()
    {
        await using var agent = new AgentTestHost(database.ConnectionString);
        using var client = agent.CreateClient();

        var paths = (await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct)).GetProperty("paths");

        var operation = paths.GetProperty("/agent").GetProperty("post");
        Assert.True(operation.TryGetProperty("requestBody", out _));
        Assert.Equal(["200", "400", "503"], operation.GetProperty("responses").EnumerateObject().Select(r => r.Name));
        Assert.True(paths.TryGetProperty("/ask", out _));
    }
}
