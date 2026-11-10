namespace TenderScout.Infrastructure.Answering;

/// <summary>
/// Settings of the chat model that writes answers, bound from the <c>Chat</c> section.
/// </summary>
public sealed class ChatModelOptions
{
    public const string SectionName = "Chat";

    /// <summary>Base address of the Ollama server. Set it in user secrets or the environment, never in the repository.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Model name as Ollama knows it.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Time limit of the single attempt at an answer, including loading the model after a pause.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);
}
