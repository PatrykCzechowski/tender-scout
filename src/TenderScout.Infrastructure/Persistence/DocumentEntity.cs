using TenderScout.Domain.Documents;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Row of the <c>documents</c> table: a <see cref="TenderDocument"/> plus bookkeeping columns.
/// </summary>
public sealed class DocumentEntity
{
    public long Id { get; private set; }

    public required string DocId { get; init; }

    public required string PublicationNumber { get; set; }

    public required string Kind { get; set; }

    public required string Path { get; set; }

    public required string Sha256 { get; set; }

    public DocumentSkipReason? SkipReason { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<DocumentChunkEntity> Chunks { get; init; } = [];

    internal static DocumentEntity Create(TenderDocument document, DateTimeOffset now) =>
        new()
        {
            DocId = document.DocId,
            PublicationNumber = document.PublicationNumber,
            Kind = document.Kind,
            Path = document.Path,
            Sha256 = document.Sha256,
            SkipReason = document.SkipReason,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void Apply(TenderDocument document, DateTimeOffset now)
    {
        PublicationNumber = document.PublicationNumber;
        Kind = document.Kind;
        Path = document.Path;
        Sha256 = document.Sha256;
        SkipReason = document.SkipReason;
        UpdatedAt = now;
    }
}
