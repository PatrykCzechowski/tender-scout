namespace TenderScout.Domain.Documents;

/// <summary>A chunk waiting for an embedding.</summary>
public sealed record ChunkToEmbed(long ChunkId, string Text);

/// <summary>The vector generated for a chunk.</summary>
public sealed record ChunkEmbedding(long ChunkId, ReadOnlyMemory<float> Vector)
{
    /// <summary>Length of every vector; fixed by the <c>vector(1024)</c> column.</summary>
    public const int Dimensions = 1024;
}

/// <summary>A chunk found by vector or hybrid search.</summary>
/// <param name="ChunkId">The chunk.</param>
/// <param name="DocId">Manifest id of the document the chunk belongs to.</param>
/// <param name="PageNumber">One-based page where the chunk's own text starts.</param>
/// <param name="Text">The passage.</param>
/// <param name="Score">
/// Higher is better. Cosine similarity for vector search; for hybrid search the Reciprocal Rank Fusion score.
/// </param>
public sealed record ChunkSearchResult(long ChunkId, string DocId, int PageNumber, string Text, double Score);
