using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TenderScout.Tests.Shared;

/// <summary>Answers each request with the next scripted chat-completions response, in the shape OpenRouter returns.</summary>
internal sealed class FakeOpenRouterHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    public List<(string Uri, string? Authorization, JsonNode Body)> Requests { get; } = [];

    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    /// <param name="usage">False leaves out the token counts, as some providers do.</param>
    public static HttpResponseMessage Answer(string content, bool usage = true) => Completion(
        new JsonObject { ["role"] = "assistant", ["content"] = content }, "stop", usage);

    public static HttpResponseMessage ToolCall(string id, string name, string arguments) => Completion(
        new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
            }),
        },
        "tool_calls");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((
            request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString(),
            JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!));

        await Task.Delay(Delay, cancellationToken);
        return _responses.Dequeue();
    }

    private static HttpResponseMessage Completion(JsonObject message, string finishReason, bool usage = true)
    {
        var body = new JsonObject
        {
            ["id"] = "gen-1",
            ["object"] = "chat.completion",
            ["created"] = 1_794_000_000,
            ["model"] = "openai/gpt-4.1-mini",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finishReason }),
        };
        if (usage)
        {
            body["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
