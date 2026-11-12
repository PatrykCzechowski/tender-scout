using NpgsqlTypes;
using Pgvector;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Row of the <c>document_chunks</c> table. Its id stays the same while the document's hash does.
/// </summary>
public sealed class DocumentChunkEntity
{
    public long Id { get; private set; }

    public long DocumentId { get; init; }

    public int Position { get; init; }

    public int PageNumber { get; init; }

    public required string Text { get; init; }

    /// <summary>The chunk's vector; <see langword="null"/> until it has been embedded.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>The model that produced <see cref="Embedding"/>.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Full-text form of <see cref="Text"/>, computed by the database.</summary>
    public NpgsqlTsVector SearchVector { get; private set; } = null!;
}
