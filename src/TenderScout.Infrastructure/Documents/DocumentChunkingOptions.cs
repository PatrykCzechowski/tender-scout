namespace TenderScout.Infrastructure.Documents;

/// <summary>
/// How documents are split into chunks, bound from the <c>DocumentChunking</c> section.
/// </summary>
public sealed class DocumentChunkingOptions
{
    public const string SectionName = "DocumentChunking";

    /// <summary>Maximum chunk length in characters, overlap included.</summary>
    public int ChunkSize { get; set; } = 1200;

    /// <summary>Maximum length in characters of the text a chunk repeats from the previous one.</summary>
    public int ChunkOverlap { get; set; } = 150;
}
