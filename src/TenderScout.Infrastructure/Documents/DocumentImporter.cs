using System.Security.Cryptography;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenderScout.Contracts.Documents;

namespace TenderScout.Infrastructure.Documents;

/// <summary>What one import run did.</summary>
/// <param name="Published">Rows that passed their checks and were sent for processing.</param>
/// <param name="Ignored">Rows of a kind outside <see cref="DocumentImportOptions.DocKinds"/>.</param>
/// <param name="Rejected">Rows that could not be sent; each message names the document and the problem.</param>
public sealed record DocumentImportReport(int Published, int Ignored, IReadOnlyList<string> Rejected)
{
    /// <summary>A rejected row needs attention and fails the run.</summary>
    public bool Succeeded => Rejected.Count == 0;
}

/// <summary>
/// Checks the documents listed in the manifest and publishes one <see cref="DocumentUploaded"/> per valid row.
/// A rejected row does not stop the others. Every valid row is published on every run, because processing
/// an unchanged file again costs nothing, so running the import again repairs anything that failed.
/// </summary>
public sealed partial class DocumentImporter(
    IPublishEndpoint publishEndpoint,
    IOptions<DocumentImportOptions> options,
    ILogger<DocumentImporter> logger)
{
    /// <exception cref="DocumentImportException">The manifest itself cannot be used; nothing was published.</exception>
    public async Task<DocumentImportReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var rows = ReadManifest(settings.ManifestPath);
        var kinds = new HashSet<string>(settings.DocKinds, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var rejected = new List<string>();
        var published = 0;
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
                await PublishRowAsync(row, settings, seen, cancellationToken).ConfigureAwait(false);
                published++;
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

        LogFinished(logger, published, ignored, rejected.Count);
        return new DocumentImportReport(published, ignored, rejected);
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

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
    }

    private async Task PublishRowAsync(
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

        // Mandatory: without a bound queue the broker would drop the message and the run would report success.
        await publishEndpoint.Publish(
            new DocumentUploaded(row.DocId, row.PublicationNumber, row.Kind, row.Path, path),
            context => context.Mandatory = true,
            cancellationToken).ConfigureAwait(false);
    }

    private void Reject(List<string> rejected, string message, Exception exception)
    {
        rejected.Add(message);
        LogRejected(logger, message, exception);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document import finished: {Published} published, {Ignored} ignored, {Rejected} rejected")]
    private static partial void LogFinished(ILogger logger, int published, int ignored, int rejected);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rejected document: {Problem}")]
    private static partial void LogRejected(ILogger logger, string problem, Exception exception);
}
