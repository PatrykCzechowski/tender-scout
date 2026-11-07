namespace TenderScout.Domain.Notices;

/// <summary>
/// A public procurement notice discovered on TED (Tenders Electronic Daily).
/// </summary>
/// <param name="PublicationNumber">TED publication number, e.g. <c>722731-2026</c>; unique per notice.</param>
/// <param name="Title">Notice title, preferably in Polish; <see langword="null"/> when TED does not provide one.</param>
/// <param name="BuyerName">Name of the contracting authority; <see langword="null"/> when not provided.</param>
/// <param name="MainCpvCode">Main CPV classification of the procedure; <see langword="null"/> when not provided.</param>
/// <param name="CpvCodes">All distinct CPV codes of the notice (procedure and lots).</param>
/// <param name="PublicationDate">Date the notice was published on TED.</param>
/// <param name="SubmissionDeadline">Earliest tender submission deadline across lots; <see langword="null"/> when the notice has none (e.g. award notices).</param>
/// <param name="Link">Human-readable notice page on TED; <see langword="null"/> when not provided.</param>
public sealed record Notice(
    string PublicationNumber,
    string? Title,
    string? BuyerName,
    string? MainCpvCode,
    IReadOnlyList<string> CpvCodes,
    DateOnly PublicationDate,
    DateOnly? SubmissionDeadline,
    Uri? Link);
