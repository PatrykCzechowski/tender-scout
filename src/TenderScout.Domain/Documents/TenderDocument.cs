namespace TenderScout.Domain.Documents;

/// <summary>
/// A tender document listed in the import manifest.
/// </summary>
/// <param name="DocId">Manifest identifier; a document keeps it across new versions of its file.</param>
/// <param name="PublicationNumber">TED publication number. Not linked to a stored notice: most documents are older than the polling window.</param>
/// <param name="Kind">Document kind from the manifest, e.g. <c>swz</c>.</param>
/// <param name="Path">File path relative to the documents folder.</param>
/// <param name="Sha256">SHA-256 of the file as lowercase hex.</param>
/// <param name="SkipReason">Why the document has no chunks; <see langword="null"/> when it was imported.</param>
public sealed record TenderDocument(
    string DocId,
    string PublicationNumber,
    string Kind,
    string Path,
    string Sha256,
    DocumentSkipReason? SkipReason);

/// <summary>Why a document was recorded without chunks.</summary>
public enum DocumentSkipReason
{
    /// <summary>A PDF with pages but (almost) no extractable text, typically a scan.</summary>
    NoTextLayer,

    /// <summary>A file format the import does not read, e.g. DOC or DOCX.</summary>
    UnsupportedFormat,
}
