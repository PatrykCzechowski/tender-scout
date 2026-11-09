using System.Text;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenderScout.Contracts.Documents;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Infrastructure.Documents;

/// <summary>
/// Reads an uploaded document and stores it with its chunks, as one unit, keyed by the document id.
/// Safe to run any number of times: an unchanged file is not read or written again.
/// </summary>
public sealed partial class DocumentUploadedConsumer(
    DocumentRepository repository,
    IOptions<DocumentChunkingOptions> options,
    ILogger<DocumentUploadedConsumer> logger)
    : IConsumer<DocumentUploaded>
{
    /// <summary>Below this many non-whitespace characters in a whole PDF, it counts as a scan.</summary>
    private const int MinTextCharacters = 50;

    /// <summary>The PDF format allows the header anywhere in the first kilobyte.</summary>
    private const int PdfHeaderSearchLength = 1024;

    private static readonly byte[] PdfHeader = Encoding.ASCII.GetBytes("%PDF-");

    public async Task Consume(ConsumeContext<DocumentUploaded> context)
    {
        var message = context.Message;

        await StoreIfChangedAsync(message, context.CancellationToken).ConfigureAwait(false);

        // Published after the commit: if the process stops in between, redelivery publishes it. For a skipped
        // document it is harmless, because there are no chunks to embed.
        await context.Publish(new ChunksCreated(message.DocId), context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<(DocumentSkipReason? SkipReason, IReadOnlyList<DocumentChunk> Chunks)> ReadFileAsync(
        string docId,
        string fullPath,
        DocumentChunkingOptions settings,
        CancellationToken cancellationToken)
    {
        if (!await HasPdfHeaderAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            return Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? throw new DocumentImportException($"{docId}: '{Path.GetFileName(fullPath)}' is not a valid PDF.")
                : (DocumentSkipReason.UnsupportedFormat, []);
        }

        var pages = PdfTextExtractor.Extract(fullPath);
        if (pages.Sum(page => page.Text.Count(c => !char.IsWhiteSpace(c))) < MinTextCharacters)
        {
            return (DocumentSkipReason.NoTextLayer, []);
        }

        return (null, DocumentChunker.Split(pages, settings.ChunkSize, settings.ChunkOverlap));
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

    private async Task StoreIfChangedAsync(DocumentUploaded message, CancellationToken cancellationToken)
    {
        var hash = await DocumentImporter.HashFileAsync(message.FullPath, cancellationToken).ConfigureAwait(false);

        // The same file keeps its chunk ids and vectors.
        if (await repository.FindHashAsync(message.DocId, cancellationToken).ConfigureAwait(false) == hash)
        {
            return;
        }

        var (skipReason, chunks) = await ReadFileAsync(message.DocId, message.FullPath, options.Value, cancellationToken).ConfigureAwait(false);
        var document = new TenderDocument(message.DocId, message.PublicationNumber, message.Kind, message.Path, hash, skipReason);
        var result = await repository.SaveAsync(document, chunks, cancellationToken).ConfigureAwait(false);

        LogStored(logger, message.DocId, result);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Document {DocId}: {Result}")]
    private static partial void LogStored(ILogger logger, string docId, DocumentSaveResult result);
}
