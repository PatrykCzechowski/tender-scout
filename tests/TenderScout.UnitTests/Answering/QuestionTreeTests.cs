using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Answering;

/// <summary>
/// The trace tree of a question without any container: an in-memory ASP.NET Core server with the services' telemetry
/// settings and the real chat and embedding registrations on fake HTTP. The framework's function-invoking client runs
/// the gathering loop as the agent does, with fixed functions in place of the database-backed tools. The full agent
/// with its real tools is covered against the test database in the integration tests.
/// </summary>
public sealed class QuestionTreeTests
{
    private const string Model = "tree-model";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModelTurnsToolsAndTheSearchEmbedding_AreOneTreeUnderTheRequest()
    {
        List<Activity> spans = [];
        var chat = new FakeOpenRouterHandler(
            FakeOpenRouterHandler.ToolCall("call_1", "search_notices", """{"query":"serwer"}"""),
            FakeOpenRouterHandler.ToolCall("call_2", "search_tender_documents", """{"query":"wadium","publicationNumber":"111111-2026"}"""),
            FakeOpenRouterHandler.Answer("GOTOWE"),
            FakeOpenRouterHandler.Answer("""{"answer":"10 000 zł.","citations":[1]}"""));
        await using var app = Build(chat, spans);
        await app.StartAsync(Ct);

        var traceId = ActivityTraceId.CreateRandom();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        using var response = await client.PostAsync("/question", null, Ct);
        var trace = await TraceAsync(app, spans, traceId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(trace, s => s.Kind == ActivityKind.Server);
        var gathering = Assert.Single(trace, s => s.DisplayName == "orchestrate_tools");
        var turns = trace.Where(s => s.DisplayName == $"chat {Model}").OrderBy(s => s.StartTimeUtc).ToList();
        var notices = Assert.Single(trace, s => s.DisplayName == "execute_tool search_notices");
        var documents = Assert.Single(trace, s => s.DisplayName == "execute_tool search_tender_documents");
        var embedding = Assert.Single(trace, s => s.DisplayName == "embeddings bge-m3");
        Assert.Equal(4, turns.Count);
        Assert.Equal(request.SpanId, gathering.ParentSpanId);
        Assert.All(turns.Take(3), turn => Assert.Equal(gathering.SpanId, turn.ParentSpanId));
        Assert.Equal(request.SpanId, turns[3].ParentSpanId);
        Assert.Equal(gathering.SpanId, notices.ParentSpanId);
        Assert.Equal(gathering.SpanId, documents.ParentSpanId);
        Assert.Equal(documents.SpanId, embedding.ParentSpanId);
        Assert.True(documents.StartTimeUtc >= turns[1].StartTimeUtc + turns[1].Duration);
    }

    private static WebApplication Build(FakeOpenRouterHandler chat, List<Activity> spans)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:Provider"] = ChatModelOptions.OpenRouter,
            ["Chat:Endpoint"] = "https://openrouter.test/api/v1",
            ["Chat:Model"] = Model,
            [ChatModelOptions.ApiKeySetting] = "sk-or-test-tree",
            ["Embedding:Endpoint"] = "http://ollama.test:11434",
            ["Embedding:Model"] = "bge-m3",
        });
        builder.AddServiceDefaults();
        builder.Services.AddQuestionAnswering().ConfigurePrimaryHttpMessageHandler(() => chat);
        builder.Services.AddChunkEmbeddings().ConfigurePrimaryHttpMessageHandler(() => new FakeOllamaHandler());
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(spans));

        var app = builder.Build();
        app.MapPost("/question", async (IChatClient model, IEmbeddingGenerator<string, Embedding<float>> embeddings, CancellationToken cancellationToken) =>
        {
            AIFunction[] tools =
            [
                AIFunctionFactory.Create(() => """{"notices":[{"publicationNumber":"111111-2026"}]}""", "search_notices"),
                AIFunctionFactory.Create(
                    async (string query, string publicationNumber) =>
                    {
                        await embeddings.GenerateAsync([query], cancellationToken: cancellationToken);
                        return $$"""{"passages":[{"publicationNumber":"{{publicationNumber}}","text":"Wadium wynosi 10 000 zł."}]}""";
                    },
                    "search_tender_documents"),
            ];

            // Not disposed, as in the agent: that would dispose the shared chat client.
            var gatherer = new FunctionInvokingChatClient(model);
            await gatherer.GetResponseAsync("Jakie wadium?", new ChatOptions { Tools = [.. tools] }, cancellationToken);
            await model.GetResponseAsync("Odpowiedz ze źródeł.", cancellationToken: cancellationToken);
            return Results.Ok();
        });
        return app;
    }

    /// <summary>The trace's spans once the request's own span has ended, which is just after the response.</summary>
    private static async Task<List<Activity>> TraceAsync(WebApplication app, List<Activity> spans, ActivityTraceId traceId)
    {
        var tracing = app.Services.GetRequiredService<TracerProvider>();
        for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(10); await Task.Delay(50, Ct))
        {
            tracing.ForceFlush();
            lock (spans)
            {
                var trace = spans.Where(s => s.TraceId == traceId).ToList();
                if (trace.Any(s => s.Kind == ActivityKind.Server))
                {
                    return trace;
                }
            }
        }

        throw new TimeoutException($"No server span of trace {traceId} within 10 s.");
    }
}
