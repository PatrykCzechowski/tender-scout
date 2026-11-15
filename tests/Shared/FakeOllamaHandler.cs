using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TenderScout.Tests.Shared;

/// <summary>
/// Answers Ollama's chat and embedding requests in Ollama's own shape, with the token counts Ollama reports, so the
/// real Ollama clients run without a server. Every text gets the same vector.
/// </summary>
internal sealed class FakeOllamaHandler(string reply = "Tak", float[]? vector = null) : HttpMessageHandler
{
    public const int PromptTokens = 12;
    public const int AnswerTokens = 7;

    private readonly float[] _vector = vector ?? [.. Enumerable.Repeat(0.5f, 1024)];

    public List<string> Paths { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var answer = request.RequestUri.AbsolutePath == "/api/embed"
            ? new JsonObject
            {
                ["model"] = body["model"]!.GetValue<string>(),
                ["embeddings"] = new JsonArray([.. Inputs(body).Select(_ => (JsonNode)new JsonArray([.. _vector.Select(v => (JsonNode)v)]))]),
                ["prompt_eval_count"] = PromptTokens,
            }
            : new JsonObject
            {
                ["model"] = body["model"]!.GetValue<string>(),
                ["created_at"] = "2026-11-14T12:00:00Z",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = reply },
                ["done"] = true,
                ["prompt_eval_count"] = PromptTokens,
                ["eval_count"] = AnswerTokens,
            };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static IEnumerable<JsonNode?> Inputs(JsonNode body) =>
        body["input"] is JsonArray inputs ? inputs : [body["input"]];
}
