using Microsoft.Extensions.AI;

namespace TenderScout.IntegrationTests.Answering;

/// <summary>Stands in for the chat model: records what it was given and returns a fixed reply, or throws.</summary>
internal sealed class FakeChatClient : IChatClient
{
    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public string Reply { get; set; } = """{"answer": "Odpowiedź", "citations": [1]}""";

    public Exception? Failure { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options));
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));
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
}
