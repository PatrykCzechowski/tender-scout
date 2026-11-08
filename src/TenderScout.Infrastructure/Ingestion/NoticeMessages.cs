using TenderScout.Contracts.Notices;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Ingestion;

/// <summary>Maps between the domain <see cref="Notice"/> and its message contract.</summary>
internal static class NoticeMessages
{
    internal static NoticeDiscovered ToMessage(this Notice notice) =>
        new(
            notice.PublicationNumber,
            notice.Title,
            notice.BuyerName,
            notice.MainCpvCode,
            notice.CpvCodes,
            notice.PublicationDate,
            notice.SubmissionDeadline,
            notice.Link);

    internal static Notice ToNotice(this NoticeDiscovered message) =>
        new(
            message.PublicationNumber,
            message.Title,
            message.BuyerName,
            message.MainCpvCode,
            message.CpvCodes ?? [],
            message.PublicationDate,
            message.SubmissionDeadline,
            message.Link);
}
