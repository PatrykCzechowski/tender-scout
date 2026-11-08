using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;

namespace TenderScout.Infrastructure.Documents;

/// <summary>What one import run did.</summary>
/// <param name="Imported">Documents stored for the first time with chunks.</param>
/// <param name="Updated">Documents whose file changed and whose chunks were rebuilt.</param>
/// <param name="Unchanged">Documents already stored with the same hash, skipped ones included.</param>
/// <param name="Skipped">Documents newly recorded without chunks (no text layer, unsupported format).</param>
/// <param name="Ignored">Rows of a kind outside <see cref="DocumentImportOptions.DocKinds"/>.</param>
/// <param name="Rejected">Rows that could not be imported; each message names the document and the problem.</param>
public sealed record DocumentImportReport(
    int Imported,
    int Updated,
    int Unchanged,
    int Skipped,
    int Ignored,
    IReadOnlyList<string> Rejected)
{
    /// <summary>A skipped document is a known outcome; a rejected one needs attention and fails the run.</summary>
    public bool Succeeded => Rejected.Count == 0;
}

/// <summary>
/// Imports the documents listed in the manifest. A rejected row does not stop the others, and each document
/// is stored with its chunks as one unit, so a failed run can simply be started again.
/// </summary>
public sealed partial class DocumentImporter(
    IDocumentRepository repository,
    IOptions<DocumentImportOptions> options,
    ILogger<DocumentImporter> logger)
{
    /// <summary>Below this many non-whitespace characters in a whole PDF, it counts as a scan.</summary>
    internal const int MinTextCharacters = 50;

    /// <summary>The PDF format allows the header anywhere in the first kilobyte.</summary>
    private const int PdfHeaderSearchLength = 1024;

    private static readonly byte[] PdfHeader = Encoding.ASCII.GetBytes("%PDF-");

    private enum RowOutcome
    {
        Imported,
        Updated,
        Unchanged,
        Skipped,
    }

    /// <exception cref="DocumentImportException">The manifest itself cannot be used; nothing was stored.</exception>
    public async Task<DocumentImportReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var rows = ReadManifest(settings.ManifestPath);
        var kinds = new HashSet<string>(settings.DocKinds, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var outcomes = new Dictionary<RowOutcome, int>();
        var rejected = new List<string>();
        var ignored = 0;

        foreach (var row in rows)
        {
            if (!kinds.Contains(row.Kind))
            {
                ignored++;
                continue;
            }

            try
            {
                var outcome = await ImportRowAsync(row, settings, seen, cancellationToken).ConfigureAwait(false);
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            }
            catch (DocumentImportException ex)
            {
                Reject(rejected, ex.Message, ex);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Reject(rejected, $"{row.Label}: import failed: {ex.Message}", ex);
            }
        }

        var report = new DocumentImportReport(
            outcomes.GetValueOrDefault(RowOutcome.Imported),
            outcomes.GetValueOrDefault(RowOutcome.Updated),
            outcomes.GetValueOrDefault(RowOutcome.Unchanged),
            outcomes.GetValueOrDefault(RowOutcome.Skipped),
            ignored,
            rejected);

        LogFinished(logger, report.Imported, report.Updated, report.Unchanged, report.Skipped, report.Ignored, report.Rejected.Count);
        return report;
    }

    internal static string ResolvePath(string documentsFolder, ManifestRow row)
    {
        if (Path.IsPathRooted(row.Path))
        {
            throw new DocumentImportException($"{row.Label}: path '{row.Path}' must be relative to the documents folder.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(documentsFolder)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(row.Path, root);

        // Compares resolved paths rather than searching for "..", which valid file names can contain.
        return fullPath.StartsWith(root, StringComparison.Ordinal)
            ? fullPath
            : throw new DocumentImportException($"{row.Label}: path '{row.Path}' is outside the documents folder.");
    }

    private static IReadOnlyList<ManifestRow> ReadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new DocumentImportException($"Manifest '{manifestPath}' does not exist.");
        }

        using var stream = File.OpenRead(manifestPath);
        return DocumentManifest.Read(stream);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
    }

    private static async Task<bool> HasPdfHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[PdfHeaderSearchLength];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return buffer.AsSpan(0, read).IndexOf(PdfHeader) >= 0;
        }
    }

    private async Task<RowOutcome> ImportRowAsync(
        ManifestRow row,
        DocumentImportOptions settings,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        row.Validate();

        if (!seen.Add(row.DocId))
        {
            throw new DocumentImportException($"{row.Label}: listed more than once in the manifest (row {row.RowNumber}).");
        }

        var path = ResolvePath(settings.DocumentsFolder, row);
        if (!File.Exists(path))
        {
            throw new DocumentImportException($"{row.Label}: file '{row.Path}' does not exist.");
        }

        var expected = row.Sha256.ToLowerInvariant();
        var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (actual != expected)
        {
            throw new DocumentImportException($"{row.Label}: SHA-256 mismatch, expected {expected}, actual {actual}.");
        }

        // The same hash means the same content. Changing only the chunk settings needs a cleared table.
        if (await repository.FindHashAsync(row.DocId, cancellationToken).ConfigureAwait(false) == actual)
        {
            return RowOutcome.Unchanged;
        }

        var (skipReason, chunks) = await ReadFileAsync(row, path, settings, cancellationToken).ConfigureAwait(false);
        var document = new TenderDocument(row.DocId, row.PublicationNumber, row.Kind, row.Path, actual, skipReason);
        var saved = await repository.SaveAsync(document, chunks, cancellationToken).ConfigureAwait(false);

        if (skipReason is { } reason)
        {
            LogSkipped(logger, row.DocId, reason);
            return RowOutcome.Skipped;
        }

        return saved == DocumentSaveResult.Inserted ? RowOutcome.Imported : RowOutcome.Updated;
    }

    private static async Task<(DocumentSkipReason? SkipReason, IReadOnlyList<DocumentChunk> Chunks)> ReadFileAsync(
        ManifestRow row,
        string path,
        DocumentImportOptions settings,
        CancellationToken cancellationToken)
    {
        if (!await HasPdfHeaderAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? throw new DocumentImportException($"{row.Label}: '{row.Path}' is not a valid PDF.")
                : (DocumentSkipReason.UnsupportedFormat, []);
        }

        var pages = PdfTextExtractor.Extract(path);
        if (pages.Sum(page => page.Text.Count(c => !char.IsWhiteSpace(c))) < MinTextCharacters)
        {
            return (DocumentSkipReason.NoTextLayer, []);
        }

        return (null, DocumentChunker.Split(pages, settings.ChunkSize, settings.ChunkOverlap));
    }

    private void Reject(List<string> rejected, string message, Exception exception)
    {
        rejected.Add(message);
        LogRejected(logger, message, exception);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document import finished: {Imported} imported, {Updated} updated, {Unchanged} unchanged, {Skipped} skipped, {Ignored} ignored, {Rejected} rejected")]
    private static partial void LogFinished(ILogger logger, int imported, int updated, int unchanged, int skipped, int ignored, int rejected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped document {DocId}: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string docId, DocumentSkipReason reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rejected document: {Problem}")]
    private static partial void LogRejected(ILogger logger, string problem, Exception exception);
}
