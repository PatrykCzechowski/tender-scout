using Microsoft.EntityFrameworkCore;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Reads stored notices. It only queries, so anything that lists notices can share it.
/// </summary>
public sealed class NoticeQueries(TenderScoutDbContext dbContext)
{
    /// <returns>The notice with that publication number, or <see langword="null"/> if none is stored.</returns>
    public async Task<Notice?> FindAsync(string publicationNumber, CancellationToken cancellationToken = default)
    {
        var notice = await dbContext.Notices
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.PublicationNumber == publicationNumber, cancellationToken)
            .ConfigureAwait(false);
        return notice?.ToNotice();
    }

    /// <summary>
    /// Returns the notices that pass every given filter, newest publication first; notices published on the same
    /// day are ordered by publication number so the order is stable.
    /// </summary>
    /// <param name="text">
    /// Words separated by whitespace. Each must occur in the title or the buyer name, in any letter case and also
    /// inside a longer word.
    /// </param>
    /// <param name="cpvPrefix">Digits the main CPV code or any other CPV code of the notice starts with.</param>
    /// <param name="deadlineFrom">Earliest submission deadline; a notice without a deadline never matches.</param>
    /// <param name="deadlineTo">Latest submission deadline; a notice without a deadline never matches.</param>
    /// <param name="limit">Maximum number of notices.</param>
    public async Task<IReadOnlyList<Notice>> SearchAsync(
        string? text,
        string? cpvPrefix,
        DateOnly? deadlineFrom,
        DateOnly? deadlineTo,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = await Filter(text, cpvPrefix, deadlineFrom, deadlineTo)
            .OrderByDescending(n => n.PublicationDate)
            .ThenBy(n => n.PublicationNumber)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(n => n.ToNotice())];
    }

    /// <summary>
    /// Returns the notices that pass the filters of <see cref="SearchAsync"/> and, when given, the documents filter,
    /// soonest submission deadline first; notices without a deadline come last, equal deadlines by publication number.
    /// </summary>
    /// <param name="hasDocuments">Only notices with (true) or without (false) a stored document of their tender.</param>
    /// <param name="limit">Maximum number of notices in the list; <see cref="NoticeList.Total"/> counts all matches.</param>
    public async Task<NoticeList> ListAsync(
        string? text,
        string? cpvPrefix,
        DateOnly? deadlineFrom,
        DateOnly? deadlineTo,
        bool? hasDocuments,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var notices = Filter(text, cpvPrefix, deadlineFrom, deadlineTo);
        if (hasDocuments is bool wanted)
        {
            notices = notices.Where(n => dbContext.Documents.Any(d => d.PublicationNumber == n.PublicationNumber) == wanted);
        }

        var total = await notices.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await notices
            .OrderBy(n => n.SubmissionDeadline == null)
            .ThenBy(n => n.SubmissionDeadline)
            .ThenBy(n => n.PublicationNumber)
            .Take(limit)
            .Select(n => new { Entity = n, HasDocuments = dbContext.Documents.Any(d => d.PublicationNumber == n.PublicationNumber) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new NoticeList(total, [.. rows.Select(row => ListedNotice.From(row.Entity.ToNotice(), row.HasDocuments))]);
    }

    private IQueryable<NoticeEntity> Filter(string? text, string? cpvPrefix, DateOnly? deadlineFrom, DateOnly? deadlineTo)
    {
        var notices = dbContext.Notices.AsNoTracking();

        foreach (var word in (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // The word is searched as it is: the characters LIKE treats as wildcards are escaped.
            var pattern = $"%{word.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal)}%";
            notices = notices.Where(n => EF.Functions.ILike(n.Title!, pattern, @"\") || EF.Functions.ILike(n.BuyerName!, pattern, @"\"));
        }

        if (cpvPrefix is not null)
        {
            notices = notices.Where(n => n.MainCpvCode!.StartsWith(cpvPrefix) || n.CpvCodes.Any(code => code.StartsWith(cpvPrefix)));
        }

        if (deadlineFrom is not null)
        {
            notices = notices.Where(n => n.SubmissionDeadline >= deadlineFrom);
        }

        if (deadlineTo is not null)
        {
            notices = notices.Where(n => n.SubmissionDeadline <= deadlineTo);
        }

        return notices;
    }
}

/// <summary>A page of notices: the first ones in order and how many match in total.</summary>
public sealed record NoticeList(int Total, IReadOnlyList<ListedNotice> Notices);

/// <summary>Every field of <see cref="Notice"/>, and whether a document of its tender is stored.</summary>
public sealed record ListedNotice(
    string PublicationNumber,
    string? Title,
    string? BuyerName,
    string? MainCpvCode,
    IReadOnlyList<string> CpvCodes,
    DateOnly PublicationDate,
    DateOnly? SubmissionDeadline,
    Uri? Link,
    bool HasDocuments)
{
    internal static ListedNotice From(Notice n, bool hasDocuments) =>
        new(n.PublicationNumber, n.Title, n.BuyerName, n.MainCpvCode, n.CpvCodes, n.PublicationDate, n.SubmissionDeadline, n.Link, hasDocuments);
}
