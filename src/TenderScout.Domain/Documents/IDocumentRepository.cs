namespace TenderScout.Domain.Documents;

/// <summary>
/// Stores imported documents and their chunks, keyed by the manifest document id.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>Returns the stored file hash of a document, or <see langword="null"/> if it has not been stored yet.</summary>
    Task<string?> FindHashAsync(string docId, CancellationToken cancellationToken = default);

    /// <summary>Returns whether a stored document has any chunks; a skipped document has none.</summary>
    Task<bool> HasChunksAsync(string docId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the document and replaces its chunks, as one unit: on failure nothing changes.
    /// </summary>
    Task<DocumentSaveResult> SaveAsync(
        TenderDocument document,
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IDocumentRepository.SaveAsync"/> did with a document.</summary>
public enum DocumentSaveResult
{
    Inserted,
    Updated,
}
