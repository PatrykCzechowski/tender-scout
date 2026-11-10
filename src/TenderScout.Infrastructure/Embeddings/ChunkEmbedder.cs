using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Infrastructure.Embeddings;

/// <summary>What one embedding run did.</summary>
public sealed record ChunkEmbeddingReport(int Embedded, int Batches);

/// <summary>
/// Embeds the chunks of a document that have no vector from the configured model, one saved batch at a time.
/// Called by the <c>ChunksCreated</c> consumer: a failed batch fails the message, which is retried and then moved
/// to the error queue; finished batches stay stored, so the next delivery embeds only what is left.
/// </summary>
public sealed partial class ChunkEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    ChunkEmbeddingRepository repository,
    IOptions<EmbeddingOptions> options,
    ILogger<ChunkEmbedder> logger)
{
    /// <summary>Embeds the pending chunks of one document.</summary>
    /// <exception cref="ChunkEmbeddingException">A batch failed; the message says which one and why.</exception>
    public async Task<ChunkEmbeddingReport> RunForDocumentAsync(string docId, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var embedded = 0;
        var batches = 0;

        while (true)
        {
            var batch = await repository
                .GetPendingAsync(settings.Model, docId, settings.BatchSize, cancellationToken)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                break;
            }

            var vectors = await GenerateAsync(batch, docId, batches + 1, embedded, cancellationToken).ConfigureAwait(false);
            await repository.SaveAsync(settings.Model, vectors, cancellationToken).ConfigureAwait(false);

            embedded += batch.Count;
            batches++;
            LogBatchStored(logger, batches, embedded);
        }

        LogFinished(logger, embedded, batches, settings.Model);
        return new ChunkEmbeddingReport(embedded, batches);
    }

    private static List<ChunkEmbedding> Validate(IReadOnlyList<ChunkToEmbed> batch, IList<Embedding<float>> generated, string where)
    {
        if (generated.Count != batch.Count)
        {
            throw new ChunkEmbeddingException($"{where}: expected {batch.Count} vectors, got {generated.Count}.");
        }

        var vectors = new List<ChunkEmbedding>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            var vector = generated[i].Vector;
            if (vector.Length != ChunkEmbedding.Dimensions)
            {
                throw new ChunkEmbeddingException($"{where}: expected {ChunkEmbedding.Dimensions} dimensions, got {vector.Length} for chunk {batch[i].ChunkId}.");
            }

            vectors.Add(new ChunkEmbedding(batch[i].ChunkId, vector));
        }

        return vectors;
    }

    private async Task<List<ChunkEmbedding>> GenerateAsync(
        IReadOnlyList<ChunkToEmbed> batch,
        string docId,
        int batchNumber,
        int alreadyStored,
        CancellationToken cancellationToken)
    {
        var where = $"Document {docId}, batch {batchNumber} (chunks {batch[0].ChunkId}..{batch[^1].ChunkId}), {alreadyStored} chunks stored in this run";

        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await generator
                .GenerateAsync(batch.Select(chunk => chunk.Text), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChunkEmbeddingException($"{where}: the embedding generator failed: {ex.Message}", ex);
        }

        return Validate(batch, generated, where);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stored batch {Batch}, {Embedded} chunks embedded so far")]
    private static partial void LogBatchStored(ILogger logger, int batch, int embedded);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding finished: {Embedded} chunks embedded in {Batches} batches with model {Model}")]
    private static partial void LogFinished(ILogger logger, int embedded, int batches, string model);
}
