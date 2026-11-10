using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TenderScout.Domain.Documents;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Stores chunk embeddings and finds the chunks nearest to a query vector.
/// </summary>
public sealed class ChunkEmbeddingRepository(TenderScoutDbContext dbContext)
{
    /// <summary>An HNSW scan returns at most <c>hnsw.ef_search</c> rows, 40 by default.</summary>
    internal const int MaxSearchResults = 40;

    /// <summary>Returns the chunks of a document without an embedding from <paramref name="model"/>, in id order.</summary>
    public async Task<IReadOnlyList<ChunkToEmbed>> GetPendingAsync(
        string model,
        string docId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await dbContext.DocumentChunks
            .AsNoTracking()
            .Where(c => dbContext.Documents.Any(d => d.Id == c.DocumentId && d.DocId == docId))
            .Where(c => c.Embedding == null || c.EmbeddingModel != model)
            .OrderBy(c => c.Id)
            .Take(limit)
            .Select(c => new ChunkToEmbed(c.Id, c.Text))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Stores the vectors and the model name as one unit. A chunk deleted in the meantime is not an error:
    /// a re-import replaced it and its successor is still waiting for an embedding.
    /// </summary>
    public Task SaveAsync(
        string model,
        IReadOnlyList<ChunkEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        // Aspire enables retry-on-failure, so a transaction opened here must run inside the execution strategy.
        return dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            ct => SaveOnceAsync(model, embeddings, ct),
            cancellationToken);
    }

    /// <summary>
    /// Returns the chunks nearest to the query vector, best first, among those embedded by <paramref name="model"/>:
    /// vectors from different models cannot be compared.
    /// </summary>
    /// <exception cref="ArgumentException">The limit is out of range or the vector has the wrong length.</exception>
    public async Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        string model,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxSearchResults);
        if (queryVector.Length != ChunkEmbedding.Dimensions)
        {
            throw new ArgumentException(
                $"The query vector must have {ChunkEmbedding.Dimensions} dimensions, not {queryVector.Length}.",
                nameof(queryVector));
        }

        var query = new Vector(queryVector);

        // The limit stays next to the ordering so PostgreSQL can answer it from the HNSW index;
        // the document id is joined on afterwards.
        var nearest = dbContext.DocumentChunks
            .AsNoTracking()
            .Where(c => c.Embedding != null && c.EmbeddingModel == model)
            .OrderBy(c => c.Embedding!.CosineDistance(query))
            .Take(limit)
            .Select(c => new { c.Id, c.DocumentId, c.PageNumber, c.Text, Distance = c.Embedding!.CosineDistance(query) });

        var rows = await nearest
            .Join(
                dbContext.Documents,
                chunk => chunk.DocumentId,
                document => document.Id,
                (chunk, document) => new { chunk.Id, document.DocId, chunk.PageNumber, chunk.Text, chunk.Distance })
            .OrderBy(row => row.Distance)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new ChunkSearchResult(row.Id, row.DocId, row.PageNumber, row.Text, 1 - row.Distance))];
    }

    private async Task SaveOnceAsync(string model, IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var embedding in embeddings)
            {
                var vector = new Vector(embedding.Vector);

                // A bulk update affects no row if the chunk is gone, where a tracked entity would throw.
                await dbContext.DocumentChunks
                    .Where(c => c.Id == embedding.ChunkId)
                    .ExecuteUpdateAsync(
                        update => update
                            .SetProperty(c => c.Embedding, vector)
                            .SetProperty(c => c.EmbeddingModel, model),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
