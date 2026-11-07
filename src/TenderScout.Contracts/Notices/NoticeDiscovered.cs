namespace TenderScout.Contracts.Notices;

/// <summary>
/// Published once per notice each time the TED poller sees it. The same notice is published again on
/// later polls (the look-back window overlaps), so consumers must be idempotent by
/// <see cref="PublicationNumber"/>.
/// </summary>
/// <remarks>
/// A message contract, deliberately separate from the domain <c>Notice</c> so the wire format only
/// changes on purpose. Add fields as optional; never rename or remove one that consumers read.
/// </remarks>
public sealed record NoticeDiscovered(
    string PublicationNumber,
    string? Title,
    string? BuyerName,
    string? MainCpvCode,
    IReadOnlyList<string> CpvCodes,
    DateOnly PublicationDate,
    DateOnly? SubmissionDeadline,
    Uri? Link);
