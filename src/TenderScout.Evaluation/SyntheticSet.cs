using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

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

    /// <summary>Four decimals keep the file small; the README records that rounding changed no ranking.</summary>
    private const int Decimals = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
}
