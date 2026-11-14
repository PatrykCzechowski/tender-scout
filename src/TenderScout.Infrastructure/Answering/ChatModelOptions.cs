namespace TenderScout.Infrastructure.Answering;

/// <summary>
/// Settings of the chat model that writes answers, bound from the <c>Chat</c> section.
/// </summary>
public sealed class ChatModelOptions
{
    public const string SectionName = "Chat";

    public const string Ollama = "Ollama";

    public const string OpenRouter = "OpenRouter";

    /// <summary>The configuration key of the OpenRouter key: a user secret or an environment variable, nothing else.</summary>
    public const string ApiKeySetting = "OPENROUTER_API_KEY";

    /// <summary><see cref="Ollama"/> (the default, local) or <see cref="OpenRouter"/> (hosted).</summary>
    public string Provider { get; set; } = Ollama;

    /// <summary>
    /// Base address of the Ollama server, or of the OpenRouter API. Set it in user secrets or the environment, never
    /// in the repository.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Model name as the provider knows it.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Read from <see cref="ApiKeySetting"/>, not from the <c>Chat</c> section; needed only for OpenRouter.</summary>
    public string? ApiKey { get; set; }

    public bool IsOpenRouter => string.Equals(Provider, OpenRouter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Time limit of the single attempt at an answer, including loading the model after a pause.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);
}
