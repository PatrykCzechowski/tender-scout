using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace TenderScout.Infrastructure.Documents;

/// <summary>One data row of the manifest, as read. <see cref="Validate"/> decides whether it can be imported.</summary>
/// <param name="RowNumber">One-based data row, for errors about rows without a usable id.</param>
internal sealed partial record ManifestRow(
    int RowNumber,
    string DocId,
    string PublicationNumber,
    string Kind,
    string Path,
    string Sha256)
{
    /// <summary>Names the row in errors.</summary>
    public string Label => string.IsNullOrWhiteSpace(DocId) ? $"row {RowNumber}" : DocId;

    /// <exception cref="DocumentImportException">A required value is blank, or the hash is malformed.</exception>
    public void Validate()
    {
        foreach (var (column, value) in new[]
        {
            (DocumentManifest.DocIdColumn, DocId),
            (DocumentManifest.PublicationNumberColumn, PublicationNumber),
            (DocumentManifest.PathColumn, Path),
            (DocumentManifest.Sha256Column, Sha256),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new DocumentImportException($"{Label}: column '{column}' is blank.");
            }
        }

        if (!Sha256Format().IsMatch(Sha256))
        {
            throw new DocumentImportException($"{Label}: '{Sha256}' is not a SHA-256 of 64 hex characters.");
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Format();
}

/// <summary>Reads the CSV manifest of tender documents.</summary>
internal static class DocumentManifest
{
    public const string DocIdColumn = "doc_id";
    public const string PublicationNumberColumn = "publication_number";
    public const string PathColumn = "path";
    public const string Sha256Column = "sha256";
    public const string KindColumn = "doc_kind";

    /// <summary>The kind of a row in a manifest that does not state one.</summary>
    public const string DefaultKind = "swz";

    private static readonly string[] RequiredColumns = [DocIdColumn, PublicationNumberColumn, PathColumn, Sha256Column];

    /// <exception cref="DocumentImportException">The manifest is empty, malformed or lacks a required column.</exception>
    public static IReadOnlyList<ManifestRow> Read(Stream csv)
    {
        using var parser = new TextFieldParser(csv, Encoding.UTF8, detectEncoding: true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");

        try
        {
            var header = parser.ReadFields() ?? throw new DocumentImportException("The manifest is empty.");
            var columns = IndexColumns(header);

            var rows = new List<ManifestRow>();
            while (parser.ReadFields() is { } fields)
            {
                rows.Add(new ManifestRow(
                    rows.Count + 1,
                    Field(fields, columns, DocIdColumn),
                    Field(fields, columns, PublicationNumberColumn),
                    Field(fields, columns, KindColumn) is { Length: > 0 } kind ? kind : DefaultKind,
                    Field(fields, columns, PathColumn),
                    Field(fields, columns, Sha256Column)));
            }

            return rows;
        }
        catch (MalformedLineException ex)
        {
            throw new DocumentImportException($"The manifest is malformed at line {ex.LineNumber}.", ex);
        }
    }

    private static Dictionary<string, int> IndexColumns(string[] header)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            columns.TryAdd(header[i].Trim(), i);
        }

        foreach (var required in RequiredColumns)
        {
            if (!columns.ContainsKey(required))
            {
                throw new DocumentImportException($"The manifest has no '{required}' column.");
            }
        }

        return columns;
    }

    private static string Field(string[] fields, Dictionary<string, int> columns, string column) =>
        columns.TryGetValue(column, out var index) && index < fields.Length ? fields[index].Trim() : string.Empty;
}
