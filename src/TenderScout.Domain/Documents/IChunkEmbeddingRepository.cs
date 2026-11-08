namespace TenderScout.Domain.Documents;

/// <summary>
/// Stores chunk embeddings and finds the chunks nearest to a query vector.
/// </summary>
public interface IChunkEmbeddingRepository
{
    /// <summary>
    /// Returns chunks without an embedding from <paramref name="model"/>, in id order, starting after
    /// <paramref name="afterChunkId"/>.
    /// </summary>
    Task<IReadOnlyList<ChunkToEmbed>> GetPendingAsync(
        string model,
        long afterChunkId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the vectors and the model name as one unit. A chunk deleted in the meantime is not an error:
    /// a re-import replaced it and its successor is still waiting for an embedding.
    /// </summary>
    Task SaveAsync(
        string model,
        IReadOnlyList<ChunkEmbedding> embeddings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the chunks nearest to the query vector, best first, among those embedded by <paramref name="model"/>:
    /// vectors from different models cannot be compared.
    /// </summary>
    /// <exception cref="ArgumentException">The limit is out of range or the vector has the wrong length.</exception>
    Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        string model,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>A chunk waiting for an embedding.</summary>
public sealed record ChunkToEmbed(long ChunkId, string Text);

/// <summary>The vector generated for a chunk.</summary>
public sealed record ChunkEmbedding(long ChunkId, ReadOnlyMemory<float> Vector)
{
    /// <summary>Length of every vector; fixed by the <c>vector(1024)</c> column.</summary>
    public const int Dimensions = 1024;
}

/// <summary>A chunk found by similarity search.</summary>
/// <param name="ChunkId">The chunk.</param>
/// <param name="DocId">Manifest id of the document the chunk belongs to.</param>
/// <param name="PageNumber">One-based page where the chunk's own text starts.</param>
/// <param name="Text">The passage.</param>
/// <param name="Score">Cosine similarity: 1 for the same direction, lower is less similar.</param>
public sealed record ChunkSearchResult(long ChunkId, string DocId, int PageNumber, string Text, double Score);
