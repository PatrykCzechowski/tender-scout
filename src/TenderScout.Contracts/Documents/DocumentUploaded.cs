namespace TenderScout.Contracts.Documents;

/// <summary>
/// Published once per manifest row that passed the import checks. Running the import again publishes it
/// again, so consumers must be idempotent by <see cref="DocId"/>.
/// </summary>
/// <remarks>
/// Carries no file hash: the consumer hashes the file it actually reads, so a replayed message stores the
/// current state of the file. Add fields as optional; never rename or remove one that consumers read.
/// </remarks>
/// <param name="DocId">Manifest identifier of the document.</param>
/// <param name="PublicationNumber">TED publication number.</param>
/// <param name="Kind">Document kind from the manifest, e.g. <c>swz</c>.</param>
/// <param name="Path">File path relative to the documents folder, as stored with the document.</param>
/// <param name="FullPath">Absolute path the consumer opens the file from.</param>
public sealed record DocumentUploaded(
    string DocId,
    string PublicationNumber,
    string Kind,
    string Path,
    string FullPath);
