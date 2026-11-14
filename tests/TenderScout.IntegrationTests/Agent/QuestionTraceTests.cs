using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.IntegrationTests.Security;
using TenderScout.IntegrationTests.Tools;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>
/// The traces of <c>/ask</c> and <c>/agent</c>, as the API exports them with its own telemetry settings, collected in
/// memory. The models are fake HTTP servers behind the real clients; the tools and the search run against the test
/// database. Each request sends a trace context of its own, so other tests' spans are told apart by trace id.
/// </summary>
public sealed class QuestionTraceTests(PostgresFixture database)
{
    private const string Secret = "Jan Kowalski, PESEL 85010112345";
    private const string AskModel = "ask-model";
    private const string AgentModel = "agent-model";

    private static readonly Notice Servers = new("111111-2026", "Dostawa serwerów", null, null, [], new DateOnly(2026, 11, 2), null, null);

    private static readonly DocumentToolTests.Chunk Deposit = new("111111-2026", "swz-a", 3, $"Wadium wynosi 10 000 zł. {Secret}", DocumentToolTests.Vector(1, 0));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ask_IsOneTree_RetrievalHoldsTheQueryEmbedding_AndTheAnswerFollowsIt()
    {
        var chat = new FakeOpenRouterHandler(FakeOpenRouterHandler.Answer($$"""{"answer":"{{Secret}}","citations":[1]}"""));
        await using var api = new TracedApi(await AgentTestHost.SeedAsync(database, [Servers], Deposit), chat, DocumentToolTests.Vector(1, 0));

        var (response, trace) = await api.PostAsync("/ask", new { question = $"Jakie jest wadium? {Secret}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = trace.Single(s => s.Kind == ActivityKind.Server);
        var retrieval = trace.Single(s => s.DisplayName == "retrieval");
        var embedding = trace.Single(s => s.DisplayName == "embeddings bge-m3");
        var answer = trace.Single(s => s.DisplayName == $"chat {AskModel}");
        Assert.Equal(request.SpanId, retrieval.ParentSpanId);
        Assert.Equal("retrieval", retrieval.GetTagItem("gen_ai.operation.name"));
        Assert.True(IsBelow(trace, embedding, retrieval));
        Assert.True(IsBelow(trace, answer, request));
        Assert.False(IsBelow(trace, answer, retrieval));
        Assert.True(answer.StartTimeUtc >= retrieval.StartTimeUtc + retrieval.Duration);
        Assert.Equal(10, answer.GetTagItem("gen_ai.usage.input_tokens"));
        AssertNoSecret(trace);
    }

    [Fact]
    public async Task IrrelevantPassages_AreRefusedAfterTheSearch_WithoutAChatSpan()
    {
        var chat = new FakeOpenRouterHandler();
        await using var api = new TracedApi(await AgentTestHost.SeedAsync(database, [Servers], Deposit), chat, DocumentToolTests.Vector(0, 1));

        var (response, trace) = await api.PostAsync("/ask", new { question = "Kto wygrał mecz?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(trace, s => s.DisplayName == "retrieval");
        Assert.Single(trace, s => s.DisplayName == "embeddings bge-m3");
        Assert.DoesNotContain(trace, s => s.DisplayName.StartsWith("chat", StringComparison.Ordinal));
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Agent_TwoSteps_EveryModelTurnAndToolIsInTheRequestsTree_AndTheSearchEmbeddingIsUnderItsTool()
    {
        var chat = new FakeOpenRouterHandler(
            FakeOpenRouterHandler.ToolCall("call_1", "search_notices", """{"query":"serwer"}"""),
            FakeOpenRouterHandler.ToolCall("call_2", "search_tender_documents", """{"query":"wadium","publicationNumber":"111111-2026"}"""),
            FakeOpenRouterHandler.Answer("GOTOWE"),
            FakeOpenRouterHandler.Answer("""{"answer":"10 000 zł.","citations":[2]}"""));
        await using var api = new TracedApi(await AgentTestHost.SeedAsync(database, [Servers], Deposit), chat, DocumentToolTests.Vector(1, 0));

        var (response, trace) = await api.PostAsync("/agent", new { question = $"Jakie wadium w przetargu na serwery? {Secret}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = trace.Single(s => s.Kind == ActivityKind.Server);
        var turns = trace.Where(s => s.DisplayName == $"chat {AgentModel}").OrderBy(s => s.StartTimeUtc).ToList();
        var notices = trace.Single(s => s.DisplayName == "execute_tool search_notices");
        var documents = trace.Single(s => s.DisplayName == "execute_tool search_tender_documents");
        Assert.Equal(4, turns.Count);
        Assert.All(turns, turn => Assert.True(IsBelow(trace, turn, request)));
        Assert.All(turns, turn => Assert.Equal(ActivityKind.Client, turn.Kind));
        Assert.All([notices, documents], tool => Assert.True(IsBelow(trace, tool, request)));
        Assert.All([notices, documents], tool => Assert.DoesNotContain(turns, turn => IsBelow(trace, tool, turn)));
        Assert.True(notices.StartTimeUtc >= turns[0].StartTimeUtc + turns[0].Duration);
        Assert.True(documents.StartTimeUtc >= turns[1].StartTimeUtc + turns[1].Duration);
        Assert.True(IsBelow(trace, trace.Single(s => s.DisplayName == "embeddings bge-m3"), documents));
        Assert.Equal("search_tender_documents", documents.GetTagItem("gen_ai.tool.name"));
        Assert.DoesNotContain(trace, s => s.DisplayName.StartsWith("chat", StringComparison.Ordinal) && (string?)s.GetTagItem("gen_ai.request.model") != AgentModel);
        AssertNoSecret(trace);
    }

    [Fact]
    public async Task ToolRejectingItsArguments_IsAFailedToolSpanWithoutTheText_AndTheCorrectedCallSucceeds()
    {
        var chat = new FakeOpenRouterHandler(
            FakeOpenRouterHandler.ToolCall("call_1", "get_notice", $$"""{"publicationNumber":"{{Secret}}"}"""),
            FakeOpenRouterHandler.ToolCall("call_2", "get_notice", """{"publicationNumber":"111111-2026"}"""),
            FakeOpenRouterHandler.Answer("GOTOWE"),
            FakeOpenRouterHandler.Answer("""{"answer":"Dostawa serwerów.","citations":[1]}"""));
        await using var api = new TracedApi(await AgentTestHost.SeedAsync(database, [Servers]), chat, DocumentToolTests.Vector(1, 0));

        var (response, trace) = await api.PostAsync("/agent", new { question = "Czego dotyczy przetarg 111111-2026?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tools = trace.Where(s => s.DisplayName == "execute_tool get_notice").OrderBy(s => s.StartTimeUtc).ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal(ActivityStatusCode.Error, tools[0].Status);
        Assert.Equal("ModelContextProtocol.McpException", tools[0].GetTagItem("error.type"));
        Assert.NotEqual(ActivityStatusCode.Error, tools[1].Status);
        AssertNoSecret(trace);
    }

    [Fact]
    public async Task CallerWithoutAToken_GetsNoModelOrToolWork()
    {
        var chat = new FakeOpenRouterHandler();
        await using var api = new TracedApi(database.ConnectionString, chat, DocumentToolTests.Vector(1, 0));

        var (response, trace) = await api.PostAsync("/agent", new { question = "Pytanie" }, token: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(trace, s => s.Kind == ActivityKind.Server);
        Assert.DoesNotContain(trace, s => s.GetTagItem("gen_ai.operation.name") is not null);
    }

    private static bool IsBelow(List<Activity> trace, Activity span, Activity ancestor)
    {
        var bySpanId = trace.ToDictionary(s => s.SpanId);
        for (var parent = span.ParentSpanId; bySpanId.TryGetValue(parent, out var next); parent = next.ParentSpanId)
        {
            if (next.SpanId == ancestor.SpanId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>No name, tag, event or status text of any span of the trace holds the test's personal data.</summary>
    private static void AssertNoSecret(List<Activity> trace)
    {
        foreach (var span in trace)
        {
            string[] exported = [span.DisplayName, span.StatusDescription ?? "", .. span.Tags.Select(t => $"{t.Key}={t.Value}"),
                .. span.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}:{t.Key}={t.Value}"))];
            Assert.DoesNotContain(exported, text => text.Contains("85010112345", StringComparison.Ordinal));
        }
    }

    /// <summary>The API with OpenRouter chat, Ollama embeddings and an in-memory trace exporter, all on fake HTTP.</summary>
    private sealed class TracedApi : IAsyncDisposable
    {
        private readonly List<Activity> _spans = [];
        private readonly WebApplicationFactory<Program> _factory;

        public TracedApi(string connection, FakeOpenRouterHandler chat, float[] queryVector)
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("ConnectionStrings:tenderscout", connection);
                web.UseSetting("Embedding:Endpoint", "http://ollama.test:11434");
                web.UseSetting("Chat:Provider", ChatModelOptions.OpenRouter);
                web.UseSetting("Chat:Endpoint", "https://openrouter.test/api/v1");
                web.UseSetting("Chat:Model", AskModel);
                web.UseSetting("Agent:Model", AgentModel);
                web.UseSetting(ChatModelOptions.ApiKeySetting, "sk-or-test-traces");
                web.UseTestIssuer();
                web.ConfigureTestServices(services =>
                {
                    services.AddHttpClient(AnsweringServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => chat);
                    services.AddHttpClient(EmbeddingServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeOllamaHandler(vector: queryVector));
                    services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(_spans));
                });
            });
        }

        /// <summary>Posts with a sampled trace context of its own and returns the response and that trace's spans.</summary>
        public async Task<(HttpResponseMessage Response, List<Activity> Trace)> PostAsync(string path, object body, bool token = true)
        {
            var traceId = ActivityTraceId.CreateRandom();
            using var client = token ? _factory.CreateClient().WithToken() : _factory.CreateClient();
            client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
            var response = await client.PostAsJsonAsync(path, body, Ct);

            // The request's span ends just after the response has been sent.
            var tracing = _factory.Services.GetRequiredService<TracerProvider>();
            for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(10); await Task.Delay(50, Ct))
            {
                tracing.ForceFlush();
                lock (_spans)
                {
                    var trace = _spans.Where(s => s.TraceId == traceId).ToList();
                    if (trace.Any(s => s.Kind == ActivityKind.Server))
                    {
                        return (response, trace);
                    }
                }
            }

            throw new TimeoutException($"No server span of trace {traceId} within 10 s.");
        }

        public ValueTask DisposeAsync() => _factory.DisposeAsync();
    }
}
