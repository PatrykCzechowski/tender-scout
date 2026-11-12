namespace TenderScout.Infrastructure.Answering;

/// <summary>An answer with its sources, or a refusal with a fixed message and no citations.</summary>
public sealed record AskResponse(bool Refused, string Answer, IReadOnlyList<Citation> Citations);

/// <summary>A passage the answer is based on, as stored, not as the model quoted it.</summary>
public sealed record Citation(string DocId, string PublicationNumber, int Page, string Passage);
