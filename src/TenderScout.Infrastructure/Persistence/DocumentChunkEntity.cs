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
}
