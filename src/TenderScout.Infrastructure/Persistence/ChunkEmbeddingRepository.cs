using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TenderScout.Domain.Documents;

namespace TenderScout.Infrastructure.Persistence;

/// <summary>
/// Stores chunk embeddings and finds the chunks nearest to a query vector.
/// </summary>
public sealed class ChunkEmbeddingRepository(TenderScoutDbContext dbContext)
{
    /// <summary>An HNSW scan returns at most <c>hnsw.ef_search</c> rows, 40 by default.</summary>
    internal const int MaxSearchResults = 40;

    /// <summary>PostgreSQL text search configuration of <c>search_vector</c>.</summary>
    internal const string TextSearchConfiguration = "simple";

    internal const int MaxHybridResults = 20;

    /// <summary>Candidates taken from each ranking; the vector side cannot return more than an HNSW scan.</summary>
    private const int CandidatesPerMethod = MaxSearchResults;

    /// <summary>The usual Reciprocal Rank Fusion constant: it damps the difference between the top ranks.</summary>
    private const int RrfConstant = 60;

    private const int MinWordLength = 3;

    /// <summary>Question and function words: frequent in questions, they say nothing about the passage.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "jaka", "jaki", "jakie", "jest", "czy", "ile", "kiedy", "gdzie", "który", "która", "które", "oraz", "się",
        "dla", "przez", "jako", "tym", "ten", "nie", "lub", "albo", "jak", "przy", "bez", "nad", "pod",
    };

    /// <summary>Returns the chunks of a document without an embedding from <paramref name="model"/>, in id order.</summary>
    public async Task<IReadOnlyList<ChunkToEmbed>> GetPendingAsync(
        string model,
        string docId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await dbContext.DocumentChunks
            .AsNoTracking()
            .Where(c => dbContext.Documents.Any(d => d.Id == c.DocumentId && d.DocId == docId))
            .Where(c => c.Embedding == null || c.EmbeddingModel != model)
            .OrderBy(c => c.Id)
            .Take(limit)
            .Select(c => new ChunkToEmbed(c.Id, c.Text))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Stores the vectors and the model name as one unit. A chunk deleted in the meantime is not an error:
    /// a re-import replaced it and its successor is still waiting for an embedding.
    /// </summary>
    public Task SaveAsync(
        string model,
        IReadOnlyList<ChunkEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        // Aspire enables retry-on-failure, so a transaction opened here must run inside the execution strategy.
        return dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            ct => SaveOnceAsync(model, embeddings, ct),
            cancellationToken);
    }

    /// <summary>
    /// Returns the chunks nearest to the query vector, best first, among those embedded by <paramref name="model"/>:
    /// vectors from different models cannot be compared.
    /// </summary>
    /// <exception cref="ArgumentException">The limit is out of range or the vector has the wrong length.</exception>
    public async Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        string model,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxSearchResults);
        if (queryVector.Length != ChunkEmbedding.Dimensions)
        {
            throw new ArgumentException(
                $"The query vector must have {ChunkEmbedding.Dimensions} dimensions, not {queryVector.Length}.",
                nameof(queryVector));
        }

        var query = new Vector(queryVector);

        // The limit stays next to the ordering so PostgreSQL can answer it from the HNSW index;
        // the document id is joined on afterwards.
        var nearest = dbContext.DocumentChunks
            .AsNoTracking()
            .Where(c => c.Embedding != null && c.EmbeddingModel == model)
            .OrderBy(c => c.Embedding!.CosineDistance(query))
            .Take(limit)
            .Select(c => new { c.Id, c.DocumentId, c.PageNumber, c.Text, Distance = c.Embedding!.CosineDistance(query) });

        var rows = await nearest
            .Join(
                dbContext.Documents,
                chunk => chunk.DocumentId,
                document => document.Id,
                (chunk, document) => new { chunk.Id, document.DocId, chunk.PageNumber, chunk.Text, chunk.Distance })
            .OrderBy(row => row.Distance)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new ChunkSearchResult(row.Id, row.DocId, row.PageNumber, row.Text, 1 - row.Distance))];
    }

    /// <summary>
    /// Combines vector search with full-text search: each ranks its candidates and Reciprocal Rank Fusion merges
    /// the two rankings, so a chunk found by either can be returned and one found by both ranks higher.
    /// </summary>
    /// <param name="queryText">The question, for the full-text side.</param>
    /// <param name="queryVector">Its embedding from <paramref name="model"/>, for the vector side.</param>
    /// <exception cref="ArgumentException">The text is blank, the limit is out of range or the vector has the wrong length.</exception>
    public async Task<IReadOnlyList<ChunkSearchResult>> HybridSearchAsync(
        string queryText,
        ReadOnlyMemory<float> queryVector,
        string model,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxHybridResults);
        var vectorRanking = await SearchAsync(queryVector, model, CandidatesPerMethod, cancellationToken).ConfigureAwait(false);
        var textRanking = await TextSearchAsync(queryText, cancellationToken).ConfigureAwait(false);

        var scores = new Dictionary<long, double>();
        foreach (var ranking in new[] { vectorRanking.Select(r => r.ChunkId).ToList(), textRanking })
        {
            for (var rank = 1; rank <= ranking.Count; rank++)
            {
                scores[ranking[rank - 1]] = scores.GetValueOrDefault(ranking[rank - 1]) + (1.0 / (RrfConstant + rank));
            }
        }

        var best = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).Take(limit).ToList();
        var ids = best.Select(s => s.Key).ToList();
        var chunks = await dbContext.DocumentChunks
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Join(dbContext.Documents, c => c.DocumentId, d => d.Id, (c, d) => new { c.Id, d.DocId, c.PageNumber, c.Text })
            .ToDictionaryAsync(c => c.Id, cancellationToken)
            .ConfigureAwait(false);

        return [.. best.Select(s => new ChunkSearchResult(s.Key, chunks[s.Key].DocId, chunks[s.Key].PageNumber, chunks[s.Key].Text, s.Value))];
    }

    /// <summary>
    /// The content words of a question joined with OR. Only letters and digits survive, so the user cannot write
    /// operators; <see langword="null"/> when no word is left.
    /// </summary>
    internal static string? ToTextQuery(string queryText) => JoinWithOr(ContentWords(queryText));

    /// <summary>The content words that contain a digit, such as CPV codes or amounts, joined with OR.</summary>
    internal static string? ToNumberQuery(string queryText) =>
        JoinWithOr(ContentWords(queryText).Where(w => w.Any(char.IsDigit)));

    private static IEnumerable<string> ContentWords(string queryText) =>
        Regex.Matches(queryText.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= MinWordLength && !StopWords.Contains(w))
            .Distinct();

    private static string? JoinWithOr(IEnumerable<string> words)
    {
        var list = words.ToList();
        return list.Count == 0 ? null : string.Join(" or ", list);
    }

    /// <summary>
    /// Chunk ids ranked by full-text match, over all chunks, whether they have a vector or not. Chunks containing a
    /// number from the question come first: PostgreSQL ranking ignores how rare a word is, so a code would otherwise
    /// lose to a common word next to it, such as "CPV".
    /// </summary>
    private async Task<List<long>> TextSearchAsync(string queryText, CancellationToken cancellationToken)
    {
        if (ToTextQuery(queryText) is not { } textQuery)
        {
            return [];
        }

        var numberQuery = ToNumberQuery(queryText) ?? textQuery;
        return await dbContext.DocumentChunks
            .AsNoTracking()
            .Where(c => c.SearchVector.Matches(EF.Functions.WebSearchToTsQuery(TextSearchConfiguration, textQuery)))
            .OrderByDescending(c => c.SearchVector.Matches(EF.Functions.WebSearchToTsQuery(TextSearchConfiguration, numberQuery)))
            .ThenByDescending(c => c.SearchVector.RankCoverDensity(EF.Functions.WebSearchToTsQuery(TextSearchConfiguration, textQuery)))
            .ThenBy(c => c.Id)
            .Take(CandidatesPerMethod)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SaveOnceAsync(string model, IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var embedding in embeddings)
            {
                var vector = new Vector(embedding.Vector);

                // A bulk update affects no row if the chunk is gone, where a tracked entity would throw.
                await dbContext.DocumentChunks
                    .Where(c => c.Id == embedding.ChunkId)
                    .ExecuteUpdateAsync(
                        update => update
                            .SetProperty(c => c.Embedding, vector)
                            .SetProperty(c => c.EmbeddingModel, model),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
