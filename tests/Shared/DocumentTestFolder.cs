using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Documents;

namespace TenderScout.Tests.Shared;

/// <summary>A temporary documents folder and the manifest describing it.</summary>
internal sealed class DocumentTestFolder : IDisposable
{
    private const string Header = "doc_id,publication_number,doc_kind,path,sha256,notes";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tender-scout-documents-");
    private readonly List<ManifestLine> _lines = [];

    public string DocumentsFolder => Path.Combine(_root.FullName, "documents");

    public string ManifestPath => Path.Combine(_root.FullName, "manifest.csv");

    public static IOptions<DocumentChunkingOptions> Chunking { get; } =
        Microsoft.Extensions.Options.Options.Create(new DocumentChunkingOptions());

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Writes a file and lists it in the manifest with its real hash, unless <paramref name="sha256"/> says otherwise.</summary>
    public void Add(string docId, string path, byte[] content, string kind = "swz", string? sha256 = null)
    {
        WriteFile(path, content);
        AddRow(docId, path, sha256 ?? Hash(content), kind);
    }

    /// <summary>Lists a row in the manifest without creating a file.</summary>
    public void AddRow(string docId, string path, string sha256, string kind = "swz") =>
        _lines.Add(new ManifestLine(docId, kind, path, sha256));

    public void WriteFile(string path, byte[] content)
    {
        var fullPath = Path.Combine(DocumentsFolder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
    }

    /// <summary>Puts a new hash in the manifest, as a new version of the document would.</summary>
    public void SetHash(string docId, byte[] content)
    {
        var index = _lines.FindIndex(line => line.DocId == docId);
        _lines[index] = _lines[index] with { Sha256 = Hash(content) };
    }

    public IOptions<DocumentImportOptions> Options()
    {
        var csv = new StringBuilder(Header).Append("\r\n");
        foreach (var line in _lines)
        {
            csv.Append($"{line.DocId},100001-2026,{line.Kind},\"{line.Path}\",{line.Sha256},\"note, with a comma\"\r\n");
        }

        File.WriteAllText(ManifestPath, csv.ToString());

        return Microsoft.Extensions.Options.Options.Create(new DocumentImportOptions
        {
            DocumentsFolder = DocumentsFolder,
            ManifestPath = ManifestPath,
            DocKinds = ["swz"],
        });
    }

    public void Dispose() => _root.Delete(recursive: true);

    private sealed record ManifestLine(string DocId, string Kind, string Path, string Sha256);
}
