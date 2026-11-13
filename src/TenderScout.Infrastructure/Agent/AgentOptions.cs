namespace TenderScout.Infrastructure.Agent;

/// <summary>
/// Settings of the agent, bound from the <c>Agent</c> section. It uses the endpoint and time limit of the
/// <c>Chat</c> section, with its own model, because the model that answers <c>/ask</c> cannot call tools.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Chat model with tool support, as Ollama knows it. Without it <c>/agent</c> answers 503.</summary>
    public string Model { get; set; } = string.Empty;
}
