namespace TenderScout.Infrastructure.Documents;

/// <summary>
/// Settings for the document import, bound from the <c>DocumentImport</c> section.
/// </summary>
public sealed class DocumentImportOptions
{
    public const string SectionName = "DocumentImport";

    /// <summary>Folder that the relative paths in the manifest point into. Stays out of git.</summary>
    public string DocumentsFolder { get; set; } = string.Empty;

    /// <summary>Path of the manifest CSV.</summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>Manifest <c>doc_kind</c> values to import; rows of any other kind are ignored.</summary>
    public List<string> DocKinds { get; set; } = [];
}
