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
    /// database that holds exactly these already is left as it is; any other data are refused and not changed.
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
            .Select(line => JsonSerializer.Deserialize<Notice>(line, Web)!).ToList();
        var passages = ReadPassages(passagesPath);
        var documents = passages.Select(p => p.DocId).Distinct().Count();

        var storedNotices = await dbContext.Notices.Select(n => n.PublicationNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        var storedPassages = await dbContext.Documents
            .SelectMany(d => d.Chunks.Select(c => d.DocId + "|" + c.PageNumber + "|" + c.Text))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var storedDocuments = await dbContext.Documents.CountAsync(cancellationToken).ConfigureAwait(false);

        if (storedNotices.Count == 0 && storedDocuments == 0)
        {
            dbContext.Notices.AddRange(notices.Select(notice => NoticeEntity.Create(notice, now)));
            AddPassages(dbContext, passages);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (true, notices.Count, documents, passages.Count);
        }

        if (storedNotices.Order().SequenceEqual(notices.Select(n => n.PublicationNumber).Order())
            && storedDocuments == documents
            && storedPassages.Order().SequenceEqual(passages.Select(p => p.DocId + "|" + p.Page + "|" + p.Text).Order()))
        {
            return (false, notices.Count, documents, passages.Count);
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
        AddPassages(dbContext, ReadPassages(path));
    }

    private static List<Passage> ReadPassages(string path) =>
        [.. File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonSerializer.Deserialize<Passage>(line, SnakeCase)!)];

    private static void AddPassages(TenderScoutDbContext dbContext, List<Passage> passages)
    {
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
            dbContext.Documents.Add(document);
        }
    }

    private sealed record Passage(string DocId, string PublicationNumber, int Page, string Text, float[] Vector);
}
