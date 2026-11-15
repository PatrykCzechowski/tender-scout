using System.Text.Json.Serialization;

namespace TenderScout.Infrastructure.Answering;

/// <summary>An answer with its sources, or a refusal with a fixed message and no citations.</summary>
/// <param name="PublicationNumber">
/// The tender the search was limited to, or the one without documents; <see langword="null"/> when the search ran
/// over all documents or the question named several tenders.
/// </param>
/// <param name="PublicationNumberSource">Where that number came from; <see langword="null"/> exactly when it is.</param>
/// <param name="RefusalReason">Why the question was not answered; <see langword="null"/> for an answer.</param>
public sealed record AskResponse(
    bool Refused,
    string Answer,
    IReadOnlyList<Citation> Citations,
    string? PublicationNumber = null,
    PublicationNumberSource? PublicationNumberSource = null,
    RefusalReason? RefusalReason = null);

/// <summary>A passage the answer is based on, as stored, not as the model quoted it.</summary>
public sealed record Citation(string DocId, string PublicationNumber, int Page, string Passage);

[JsonConverter(typeof(JsonStringEnumConverter<PublicationNumberSource>))]
public enum PublicationNumberSource
{
    /// <summary>The number field of the request, which always wins.</summary>
    [JsonStringEnumMemberName("field")]
    Field,

    /// <summary>The one tender number written in the question, with the field empty.</summary>
    [JsonStringEnumMemberName("question")]
    Question,
}

[JsonConverter(typeof(JsonStringEnumConverter<RefusalReason>))]
public enum RefusalReason
{
    /// <summary>The passages do not hold the answer, or the model cited none.</summary>
    [JsonStringEnumMemberName("noAnswer")]
    NoAnswer,

    /// <summary>The question names more than one tender and the field is empty; nothing was searched.</summary>
    [JsonStringEnumMemberName("severalTenders")]
    SeveralTenders,

    /// <summary>The tender has no stored passage; nothing was searched.</summary>
    [JsonStringEnumMemberName("noDocuments")]
    NoDocuments,
}
