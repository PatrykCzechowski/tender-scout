using Microsoft.EntityFrameworkCore;
using TenderScout.Domain.Documents;

namespace TenderScout.Infrastructure.Persistence;

internal sealed class DocumentRepository(TenderScoutDbContext dbContext, TimeProvider timeProvider) : IDocumentRepository
{
    public Task<string?> FindHashAsync(string docId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docId);

        return dbContext.Documents
            .AsNoTracking()
            .Where(d => d.DocId == docId)
            .Select(d => d.Sha256)
            .SingleOrDefaultAsync(cancellationToken);
    }

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
