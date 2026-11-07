using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Persistence;

internal sealed class NoticeRepository(TenderScoutDbContext dbContext, TimeProvider timeProvider) : INoticeRepository
{
    public async Task<NoticeUpsertResult> UpsertAsync(Notice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        try
        {
            return await UpsertOnceAsync(notice, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another writer inserted the same publication number between our lookup and insert.
            // The row exists now, so a second pass updates it (or finds it identical).
            dbContext.ChangeTracker.Clear();
            return await UpsertOnceAsync(notice, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<NoticeUpsertResult> UpsertOnceAsync(Notice notice, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Notices
            .SingleOrDefaultAsync(n => n.PublicationNumber == notice.PublicationNumber, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            dbContext.Notices.Add(NoticeEntity.Create(notice, timeProvider.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return NoticeUpsertResult.Inserted;
        }

        existing.Apply(notice);
        if (!dbContext.ChangeTracker.HasChanges())
        {
            return NoticeUpsertResult.Unchanged;
        }

        existing.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return NoticeUpsertResult.Updated;
    }
}
