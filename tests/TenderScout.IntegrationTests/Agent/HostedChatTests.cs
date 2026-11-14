using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Answering;
using TenderScout.IntegrationTests.Security;
using TenderScout.IntegrationTests.Tools;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>
/// The API with OpenRouter selected: the real chat registration and adapter, with OpenRouter's HTTP answers scripted.
/// No request leaves the machine.
/// </summary>
public sealed class HostedChatTests(PostgresFixture database)
{
    private static readonly Notice Servers = new("111111-2026", "Dostawa serwerów", null, null, [], new DateOnly(2026, 11, 2), null, null);

    private static readonly DocumentToolTests.Chunk Deposit = new("111111-2026", "swz-a", 3, "Wadium wynosi 10 000 zł.", DocumentToolTests.Vector(1, 0));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agent_SendsBothPhasesToOpenRouterWithTheAgentModelAndTheKey_NeverTheCallersToken()
    {
        var openRouter = new FakeOpenRouterHandler(
            FakeOpenRouterHandler.ToolCall("call_1", "get_notice", """{"publicationNumber":"111111-2026"}"""),
            FakeOpenRouterHandler.Answer("GOTOWE"),
            FakeOpenRouterHandler.Answer("""{"answer":"Dostawa serwerów.","citations":[1]}"""));
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]), openRouter: openRouter);

        var answer = await agent.AskAsync();

        Assert.False(answer.Refused);
        Assert.Equal("111111-2026", Assert.Single(answer.Notices).PublicationNumber);
        Assert.Equal(3, openRouter.Requests.Count);
        Assert.All(openRouter.Requests, request =>
        {
            Assert.Equal("https://openrouter.test/api/v1/chat/completions", request.Uri);
            Assert.Equal(AgentTestHost.AgentModel, request.Body["model"]!.GetValue<string>());
            Assert.Equal($"Bearer {AgentTestHost.OpenRouterKey}", request.Authorization);
        });
        Assert.Equal("call_1", openRouter.Requests[1].Body["messages"]!.AsArray()[^1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("json_schema", openRouter.Requests[2].Body["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ask_GoesToOpenRouterWithTheChatModel_AndSearchesWithTheLocalEmbeddings()
    {
        var openRouter = new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer("""{"answer":"10 000 zł.","citations":[1]}"""));
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers], Deposit), openRouter: openRouter);
        using var client = agent.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "Jakie jest wadium?" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(openRouter.Requests);
        Assert.Equal(AgentTestHost.AskModel, request.Body["model"]!.GetValue<string>());
        Assert.Contains("Wadium wynosi 10 000", request.Body["messages"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(["Jakie jest wadium?"], agent.Generator.Calls.SelectMany(call => call));
    }

    [Fact]
    public async Task LocalEmbeddingsDown_Is503_WithoutAPaidRequest()
    {
        var openRouter = new FakeOpenRouterHandler();
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers], Deposit), openRouter: openRouter);
        agent.Generator.FailOnCall = 1;
        using var client = agent.CreateClient();

        using var response = await client.PostAsJsonAsync("/ask", new { question = "Jakie jest wadium?" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(openRouter.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ProviderRefusing_Is503_WithoutItsAnswer_AndNeitherTheKeyNorItsAnswerIsLogged(HttpStatusCode status)
    {
        var openRouter = new FakeOpenRouterHandler(new HttpResponseMessage(status)
        {
            Content = new StringContent(
                new JsonObject { ["error"] = new JsonObject { ["message"] = $"upstream says {AgentTestHost.OpenRouterKey}" } }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        });
        await using var agent = new AgentTestHost(database.ConnectionString, openRouter: openRouter);

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(openRouter.Requests);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("upstream says", body, StringComparison.Ordinal);
        Assert.Contains(agent.AllLog, entry => entry.Contains($"HTTP {(int)status}", StringComparison.Ordinal));
        Assert.DoesNotContain(agent.AllLog, entry => entry.Contains("upstream says", StringComparison.Ordinal));
        Assert.DoesNotContain(agent.AllLog, entry => entry.Contains(AgentTestHost.OpenRouterKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task ApiWithOpenRouterAndNoKey_DoesNotStart_AndNamesTheSetting(string? key)
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:tenderscout", database.ConnectionString)
            .UseSetting("Chat:Provider", ChatModelOptions.OpenRouter)
            .UseSetting("Chat:Endpoint", "https://openrouter.test/api/v1")
            .UseSetting("Chat:Model", AgentTestHost.AskModel)
            .UseSetting(ChatModelOptions.ApiKeySetting, key)
            .UseTestIssuer());

        var exception = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains(ChatModelOptions.ApiKeySetting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluationWithOpenRouterAndNoKey_StopsBeforeAnyWork_AndNamesTheSetting()
    {
        var command = new ProcessStartInfo("dotnet", "TenderScout.Evaluation.dll --agent-cases")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        command.Environment.Remove(ChatModelOptions.ApiKeySetting);
        command.Environment["DOTNET_ENVIRONMENT"] = "Production";
        command.Environment["Chat__Provider"] = ChatModelOptions.OpenRouter;
        command.Environment["Chat__Endpoint"] = "https://openrouter.invalid/api/v1";
        command.Environment["Agent__Model"] = "agent-model";

        using var evaluation = Process.Start(command)!;
        var output = await evaluation.StandardOutput.ReadToEndAsync(Ct);
        var errors = await evaluation.StandardError.ReadToEndAsync(Ct);
        await evaluation.WaitForExitAsync(Ct);

        Assert.NotEqual(0, evaluation.ExitCode);
        Assert.Contains(ChatModelOptions.ApiKeySetting, errors, StringComparison.Ordinal);
        Assert.Empty(output);
    }
}
