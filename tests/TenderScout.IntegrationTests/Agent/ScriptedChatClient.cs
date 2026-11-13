using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>
/// Stands in for the chat model: returns the prepared replies in order and records every request. A request the
/// script has no reply for fails the test.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _script = new();
    private int _callIds;

    /// <summary>The messages and options of every request, in order.</summary>
    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    /// <summary>Completes when a reply added with <see cref="Waits"/> is being waited for.</summary>
    public TaskCompletionSource Waiting { get; } = new();

    /// <summary>A reply that asks for tools; the calls get the ids <c>call-1</c>, <c>call-2</c> and so on.</summary>
    public ScriptedChatClient Calls(params (string Tool, object Arguments)[] calls) =>
        Reply(new ChatMessage(ChatRole.Assistant, [.. calls.Select(call => (AIContent)new FunctionCallContent(
            $"call-{++_callIds}",
            call.Tool,
            JsonSerializer.SerializeToElement(call.Arguments).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value)))]));

    /// <summary>A reply that asks for a tool with arguments the client could not read as JSON.</summary>
    public ScriptedChatClient CallWithUnreadableArguments(string tool) =>
        Reply(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{++_callIds}", tool) { Exception = new JsonException("not JSON") }]));

    /// <summary>A plain reply, which ends the gathering.</summary>
    public ScriptedChatClient Text(string text = "GOTOWE") => Reply(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>The answering reply: an answer and the numbers of the sources it names.</summary>
    public ScriptedChatClient Answer(string answer, params int[] citations) =>
        Text(JsonSerializer.Serialize(new { answer, citations }));

    public ScriptedChatClient Fails(Exception exception)
    {
        _script.Enqueue(_ => throw exception);
        return this;
    }

    /// <summary>A reply that never comes: the request ends only when it is cancelled.</summary>
    public ScriptedChatClient Waits()
    {
        _script.Enqueue(async cancellationToken =>
        {
            Waiting.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        return this;
    }

    /// <summary>What the model was given as the result of a tool call, as JSON text.</summary>
    public string ToolResult(string callId) =>
        JsonSerializer.Serialize(Requests
            .SelectMany(r => r.Messages)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .First(result => result.CallId == callId).Result);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(([.. messages], options));
        return _script.TryDequeue(out var reply)
            ? reply(cancellationToken)
            : throw new InvalidOperationException($"The script has no reply for request {Requests.Count}.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private ScriptedChatClient Reply(ChatMessage message)
    {
        _script.Enqueue(_ => Task.FromResult(new ChatResponse(message)));
        return this;
    }
}
