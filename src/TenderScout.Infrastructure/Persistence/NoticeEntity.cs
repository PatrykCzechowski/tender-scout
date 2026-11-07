using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Row of the <c>notices</c> table: a <see cref="Notice"/> plus bookkeeping columns.
/// </summary>
public sealed class NoticeEntity
{
    public long Id { get; private set; }

    public required string PublicationNumber { get; init; }

    public string? Title { get; set; }

    public string? BuyerName { get; set; }

    public string? MainCpvCode { get; set; }

    public List<string> CpvCodes { get; set; } = [];

    public DateOnly PublicationDate { get; set; }

    public DateOnly? SubmissionDeadline { get; set; }

    public string? Link { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    internal static NoticeEntity Create(Notice notice, DateTimeOffset now)
    {
        var entity = new NoticeEntity
        {
            PublicationNumber = notice.PublicationNumber,
            CreatedAt = now,
            UpdatedAt = now,
        };
        entity.Apply(notice);
        return entity;
    }

    /// <summary>Copies the notice's fields; EF change tracking then tells whether anything actually changed.</summary>
    internal void Apply(Notice notice)
    {
        Title = notice.Title;
        BuyerName = notice.BuyerName;
        MainCpvCode = notice.MainCpvCode;
        CpvCodes = [.. notice.CpvCodes];
        PublicationDate = notice.PublicationDate;
        SubmissionDeadline = notice.SubmissionDeadline;
        Link = notice.Link?.AbsoluteUri;
    }

    internal Notice ToNotice() =>
        new(
            PublicationNumber,
            Title,
            BuyerName,
            MainCpvCode,
            CpvCodes,
            PublicationDate,
            SubmissionDeadline,
            Link is null ? null : new Uri(Link));
}
