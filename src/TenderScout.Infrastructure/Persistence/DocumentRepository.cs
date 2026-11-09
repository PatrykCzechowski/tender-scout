using Microsoft.EntityFrameworkCore;
using TenderScout.Domain.Documents;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Stores documents and their chunks, keyed by the manifest document id.
/// </summary>
public sealed class DocumentRepository(TenderScoutDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Returns the stored file hash of a document, or <see langword="null"/> if it has not been stored yet.</summary>
    public Task<string?> FindHashAsync(string docId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);

        return dbContext.Documents
            .AsNoTracking()
            .Where(d => d.DocId == docId)
            .Select(d => d.Sha256)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>Returns whether a stored document has any chunks; a skipped document has none.</summary>
    public Task<bool> HasChunksAsync(string docId, CancellationToken cancellationToken = default) =>
        dbContext.Documents
            .Where(d => d.DocId == docId)
            .SelectMany(d => d.Chunks)
            .AnyAsync(cancellationToken);

    /// <summary>Stores the document and replaces its chunks, as one unit: on failure nothing changes.</summary>
    public Task<DocumentSaveResult> SaveAsync(
        TenderDocument document,
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);

        // Aspire enables retry-on-failure, so a transaction opened here must run inside the execution strategy.
        return dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            ct => SaveOnceAsync(document, chunks, ct),
            cancellationToken);
    }

    private async Task<DocumentSaveResult> SaveOnceAsync(
        TenderDocument document,
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken)
    {
        // The context outlives one document: drop what an earlier document or failed attempt left tracked.
        dbContext.ChangeTracker.Clear();

        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var now = timeProvider.GetUtcNow();
            var entity = await dbContext.Documents
                .SingleOrDefaultAsync(d => d.DocId == document.DocId, cancellationToken)
                .ConfigureAwait(false);

            var result = entity is null ? DocumentSaveResult.Inserted : DocumentSaveResult.Updated;
            if (entity is null)
            {
                entity = DocumentEntity.Create(document, now);
                dbContext.Documents.Add(entity);
            }
            else
            {
                entity.Apply(document, now);
                await dbContext.DocumentChunks
                    .Where(c => c.DocumentId == entity.Id)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            entity.Chunks.AddRange(chunks.Select(c => new DocumentChunkEntity
            {
                Position = c.Position,
                PageNumber = c.PageNumber,
                Text = c.Text,
            }));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
