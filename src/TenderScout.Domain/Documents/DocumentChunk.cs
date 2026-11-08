namespace TenderScout.Domain.Documents;

/// <summary>
/// A passage of a document, small enough to embed and to cite.
/// </summary>
/// <param name="Position">Zero-based place of the chunk in reading order.</param>
/// <param name="PageNumber">One-based page where the chunk's own text starts, not counting text repeated as overlap.</param>
/// <param name="Text">The passage.</param>
public sealed record DocumentChunk(int Position, int PageNumber, string Text);
