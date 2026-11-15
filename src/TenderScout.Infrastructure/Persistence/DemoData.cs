using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenderScout.Domain.Documents;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// The invented notices and document passages that the compose demo starts with: the notices of the agent evaluation
/// and the passages of the search gate, with the vectors stored next to them, so loading needs no model.
/// </summary>
public static class DemoData
{
    /// <summary>The model the stored vectors come from; the search embeds questions with the same one.</summary>
    public const string EmbeddingModel = "bge-m3";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { RespectNullableAnnotations = true };

    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Loads the notices and passages into a database that holds no notice and no document, in one transaction. A
    /// database that holds exactly these already, every field from the files included, is left as it is; any other
    /// data are refused and not changed.
    /// </summary>
    /// <returns>Whether the data were loaded now, and how many notices, documents and passages there are.</returns>
    /// <exception cref="InvalidOperationException">The database holds other notices or documents.</exception>
    public static async Task<(bool Loaded, int Notices, int Documents, int Passages)> LoadAsync(
        TenderScoutDbContext dbContext,
        string noticesPath,
        string passagesPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var notices = File.ReadLines(noticesPath).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => NoticeEntity.Create(JsonSerializer.Deserialize<Notice>(line, Web)!, now)).ToList();
        var passages = ReadPassages(passagesPath);
        var documents = ToDocuments(passages);

        var storedNotices = await dbContext.Notices.Select(n => n.PublicationNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        var storedPassages = await dbContext.Documents
            .SelectMany(d => d.Chunks.Select(c => d.DocId + "|" + c.PageNumber + "|" + c.Text))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var storedDocuments = await dbContext.Documents.CountAsync(cancellationToken).ConfigureAwait(false);

        if (storedNotices.Count == 0 && storedDocuments == 0)
        {
            dbContext.Notices.AddRange(notices);
            dbContext.Documents.AddRange(documents);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (true, notices.Count, documents.Count, passages.Count);
        }

        // The keys first, which is cheap; only when they match are the full rows read, with their vectors.
        if (storedNotices.Order().SequenceEqual(notices.Select(n => n.PublicationNumber).Order())
            && storedDocuments == documents.Count
            && storedPassages.Order().SequenceEqual(passages.Select(p => p.DocId + "|" + p.Page + "|" + p.Text).Order())
            && await HoldsExactlyAsync(dbContext, Describe(notices, documents), cancellationToken).ConfigureAwait(false))
        {
            return (false, notices.Count, documents.Count, passages.Count);
        }

        throw new InvalidOperationException(
            "The database holds other notices or documents than the demo data, so nothing was loaded. The demo loads only "
            + "into an empty database: remove the demo's volumes (docker compose down --volumes) and start it again.");
    }

    /// <summary>
    /// Adds every passage of the file to the context, as a chunk of its document with its stored vector. The
    /// caller saves.
    /// </summary>
    public static void AddPassages(TenderScoutDbContext dbContext, string path)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        dbContext.Documents.AddRange(ToDocuments(ReadPassages(path)));
    }

    private static List<Passage> ReadPassages(string path) =>
        [.. File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonSerializer.Deserialize<Passage>(line, SnakeCase)!)];

    private static List<DocumentEntity> ToDocuments(List<Passage> passages)
    {
        List<DocumentEntity> documents = [];
        foreach (var group in passages.GroupBy(p => (p.DocId, p.PublicationNumber)))
        {
            var document = DocumentEntity.Create(
                new TenderDocument(group.Key.DocId, group.Key.PublicationNumber, "swz", "a.pdf", new string('a', 64), null),
                DateTimeOffset.UnixEpoch);
            document.Chunks.AddRange(group.Select((p, position) => new DocumentChunkEntity
            {
                Position = position,
                PageNumber = p.Page,
                Text = p.Text,
                Embedding = new Pgvector.Vector(p.Vector),
                EmbeddingModel = EmbeddingModel,
            }));
            documents.Add(document);
        }

        return documents;
    }

    private static async Task<bool> HoldsExactlyAsync(TenderScoutDbContext dbContext, List<string> expected, CancellationToken cancellationToken)
    {
        var notices = await dbContext.Notices.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var documents = await dbContext.Documents.AsNoTracking().Include(d => d.Chunks).ToListAsync(cancellationToken).ConfigureAwait(false);
        return Describe(notices, documents).SequenceEqual(expected);
    }

    /// <summary>
    /// Every field that comes from the files, one line per notice and per passage, sorted: the same for the rows of the
    /// database and for the files. Vectors are compared bit for bit. The times of a row and the stand-in path and hash
    /// of a document are not in the files and are left out.
    /// </summary>
    private static List<string> Describe(List<NoticeEntity> notices, List<DocumentEntity> documents) =>
    [
        .. notices.Select(n => JsonSerializer.Serialize(n.ToNotice(), Web)).Order(StringComparer.Ordinal),
        .. documents.SelectMany(d => d.Chunks.Select(c => string.Join(
                '|',
                d.DocId,
                d.PublicationNumber,
                c.Position.ToString(CultureInfo.InvariantCulture),
                c.PageNumber.ToString(CultureInfo.InvariantCulture),
                c.Text,
                c.EmbeddingModel,
                c.Embedding is null ? string.Empty : Convert.ToHexString(MemoryMarshal.AsBytes(c.Embedding.Memory.Span)))))
            .Order(StringComparer.Ordinal),
    ];

    private sealed record Passage(string DocId, string PublicationNumber, int Page, string Text, float[] Vector);
}
