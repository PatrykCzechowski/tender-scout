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

        var rows = await notices
            .OrderByDescending(n => n.PublicationDate)
            .ThenBy(n => n.PublicationNumber)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(n => n.ToNotice())];
    }
}
