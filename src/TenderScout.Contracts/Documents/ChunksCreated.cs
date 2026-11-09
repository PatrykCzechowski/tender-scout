namespace TenderScout.Contracts.Documents;

/// <summary>
/// Published after a document's chunks are committed. It is only a signal: the consumer reads the chunks
/// without an embedding from the database, because a re-import replaces chunks and their ids.
/// </summary>
/// <remarks>
/// A redelivered message is harmless, so consumers must be idempotent by <see cref="DocId"/>.
/// Add fields as optional; never rename or remove one that consumers read.
/// </remarks>
/// <param name="DocId">Manifest identifier of the document.</param>
public sealed record ChunksCreated(string DocId);
