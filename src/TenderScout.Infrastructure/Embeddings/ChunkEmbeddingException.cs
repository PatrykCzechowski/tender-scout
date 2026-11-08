namespace TenderScout.Infrastructure.Embeddings;

/// <summary>A batch could not be embedded, so the run stopped; finished batches stay stored.</summary>
public sealed class ChunkEmbeddingException : Exception
{
    public ChunkEmbeddingException(string message)
        : base(message)
    {
    }

    public ChunkEmbeddingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
