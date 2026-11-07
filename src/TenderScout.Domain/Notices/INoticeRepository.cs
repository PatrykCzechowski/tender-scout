namespace TenderScout.Domain.Notices;

/// <summary>
/// Stores discovered notices, keyed by their TED publication number.
/// </summary>
public interface INoticeRepository
{
    /// <summary>
    /// Inserts the notice if its publication number is new, updates the stored copy if any field changed,
    /// and leaves it untouched if it is identical.
    /// </summary>
    Task<NoticeUpsertResult> UpsertAsync(Notice notice, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="INoticeRepository.UpsertAsync"/> did with a notice.</summary>
public enum NoticeUpsertResult
{
    Inserted,
    Updated,
    Unchanged,
}
