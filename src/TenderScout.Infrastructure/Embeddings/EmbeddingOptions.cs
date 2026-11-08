namespace TenderScout.Infrastructure.Embeddings;

/// <summary>
/// Settings for generating chunk embeddings, bound from the <c>Embedding</c> section.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>Base address of the Ollama server. Set it in user secrets or the environment, never in the repository.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Model name, compared exactly with the name stored on each chunk.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Most chunks sent in one request; a batch is also the unit that is saved.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Time limit of one attempt at a batch. Generous, because the first request after a pause loads the model.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
