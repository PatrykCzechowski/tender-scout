using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Evaluation;

/// <summary>
/// The invented passages and questions behind the CI gate. Their vectors come from the real embedding model once,
/// so CI can search them without a model; regenerate them only when the embedding model changes.
/// </summary>
public static class SyntheticSet
{
    /// <summary>Relative to the repository root.</summary>
    public const string Folder = "tests/TenderScout.IntegrationTests/Evaluation";

    public const string PassagesFile = "synthetic-passages.jsonl";

    public const string QuestionsFile = "synthetic-questions.jsonl";

    /// <summary>The model the stored vectors come from.</summary>
    public const string EmbeddingModel = "bge-m3";

    /// <summary>Four decimals keep the file small; the README records that rounding changed no ranking.</summary>
    private const int Decimals = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Adds every passage of the file to the context, as a chunk of its document with its stored vector. The
    /// caller saves.
    /// </summary>
    public static void AddPassages(TenderScoutDbContext dbContext, string path)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var passages = File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<Passage>(line, SnakeCase)!);
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

    /// <summary>Embeds the <c>text</c> of every passage and the <c>question</c> of every question and stores the vectors.</summary>
    public static async Task WriteVectorsAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        foreach (var (file, textField) in new[] { (PassagesFile, "text"), (QuestionsFile, "question") })
        {
            var path = Path.Combine(repositoryRoot, Folder, file);
            var lines = (await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonNode.Parse(line)!.AsObject())
                .ToList();

            var embeddings = await generator.GenerateAsync(lines.Select(l => (string)l[textField]!), cancellationToken: cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i]["vector"] = new JsonArray([.. embeddings[i].Vector.ToArray().Select(v => (JsonNode)Math.Round(v, Decimals))]);
            }

            await File.WriteAllLinesAsync(path, lines.Select(l => l.ToJsonString(JsonOptions)), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record Passage(string DocId, string PublicationNumber, int Page, string Text, float[] Vector);
}
