using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Infrastructure.Answering;

/// <summary>
/// Answers a question from the stored passages with citations, or refuses when the passages do not hold the answer.
/// The prompt, the passage count and the refusal threshold are recorded in ADR-0004.
/// </summary>
public sealed partial class QuestionAnswerer(
    ChunkEmbeddingRepository repository,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IChatClient chatClient,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<QuestionAnswerer> logger)
{
    public const string RefusalMessage = "Nie znalazłem odpowiedzi w dokumentach.";

    internal const int PassageCount = 5;

    /// <summary>
    /// Lowest cosine similarity of the nearest chunk that lets a question reach the model. Provisional: the first
    /// golden-set run could not calibrate it, because the number exception applied to every question (ADR-0004).
    /// </summary>
    internal const double MinSimilarity = 0.45;

    internal const int ContextTokens = 8192;

    /// <summary>
    /// An answer with its citation numbers takes a few hundred tokens. Without a limit the model once kept writing for
    /// minutes inside the JSON; a cut-off reply is not valid JSON, so it ends as a refusal.
    /// </summary>
    internal const int MaxAnswerTokens = 512;

    internal const string AnswerSchema = """
        {"type":"object","properties":{"answer":{"type":"string"},"citations":{"type":"array","items":{"type":"integer"}}},"required":["answer","citations"]}
        """;

    internal const string SystemPrompt = $$"""
        Jesteś asystentem, który odpowiada na pytania o dokumenty przetargowe.
        Odpowiadaj po polsku i wyłącznie na podstawie podanych fragmentów.
        W polu "citations" podaj numery fragmentów z nawiasów kwadratowych, na których opierasz odpowiedź, a nie numery stron.
        Jeśli fragmenty nie zawierają odpowiedzi, zwróć pustą listę "citations".
        Zwróć wyłącznie JSON zgodny ze schematem: {{AnswerSchema}}
        """;

    private static readonly AskResponse Refusal = new(true, RefusalMessage, []);

    /// <exception cref="Exception">The embedding or chat model failed; see <see cref="IsUnavailable"/>.</exception>
    public async Task<AskResponse> AskAsync(string question, string? publicationNumber, CancellationToken cancellationToken)
    {
        var model = embeddingOptions.Value.Model;
        var vector = await generator.GenerateVectorAsync(question, cancellationToken: cancellationToken).ConfigureAwait(false);
        var passages = await repository.HybridSearchAsync(question, vector, model, PassageCount, publicationNumber, cancellationToken)
            .ConfigureAwait(false);

        // Fused scores depend only on rank, so relevance is judged by the cosine similarity of the nearest chunk.
        // No nearest chunk when only the text side found passages, e.g. none embedded yet.
        var nearest = await repository.SearchAsync(vector, model, 1, publicationNumber, cancellationToken).ConfigureAwait(false);
        var similarity = nearest.Count > 0 ? nearest[0].Score : 0;
        if (passages.Count == 0 || (similarity < MinSimilarity && !ContainsNumberFrom(question, passages)))
        {
            return Refusal;
        }

        var response = await chatClient.GetResponseAsync(BuildMessages(question, passages), CreateChatOptions(), cancellationToken)
            .ConfigureAwait(false);

        var reply = Parse(response.Text);
        var numbers = (reply?.Citations ?? []).Where(n => n >= 1 && n <= passages.Count).Distinct().ToList();
        if (numbers.Count == 0 || string.IsNullOrWhiteSpace(reply?.Answer))
        {
            LogUnusableReply(logger, response.Text);
            return Refusal;
        }

        return new AskResponse(false, reply.Answer, [.. numbers.Select(n => ToCitation(passages[n - 1]))]);
    }

    /// <summary>
    /// Whether the exception means a model cannot be reached: a connection error, an HTTP error such as a model that
    /// is not pulled, a timeout or an open circuit. A cancelled <paramref name="cancellationToken"/> is the caller leaving.
    /// </summary>
    public static bool IsUnavailable(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or ExecutionRejectedException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    internal static ChatOptions CreateChatOptions() => new()
    {
        Temperature = 0,
        MaxOutputTokens = MaxAnswerTokens,
        ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(AnswerSchema).RootElement, "answer"),
        AdditionalProperties = new() { ["num_ctx"] = ContextTokens },
    };

    /// <summary>A question with a code or amount may be answerable even when its vector is far from every chunk.</summary>
    internal static bool ContainsNumberFrom(string question, IReadOnlyList<ChunkSearchResult> passages)
    {
        var numbers = ChunkEmbeddingRepository.NumberWords(question).ToList();
        return passages.Any(p => numbers.Any(n => p.Text.Contains(n, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<ChatMessage> BuildMessages(string question, IReadOnlyList<ChunkSearchResult> passages)
    {
        var prompt = new StringBuilder().Append("Pytanie: ").AppendLine(question).AppendLine().AppendLine("Fragmenty:");
        for (var i = 0; i < passages.Count; i++)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"[{i + 1}] dokument {passages[i].DocId}, strona {passages[i].PageNumber}")
                .AppendLine(passages[i].Text)
                .AppendLine();
        }

        return [new(ChatRole.System, SystemPrompt), new(ChatRole.User, prompt.ToString())];
    }

    private static ModelReply? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelReply>(text, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Citation ToCitation(ChunkSearchResult passage) =>
        new(passage.DocId, passage.PublicationNumber, passage.PageNumber, passage.Text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The chat model gave no valid JSON answer with a valid citation; refusing. Reply: {Reply}")]
    private static partial void LogUnusableReply(ILogger logger, string reply);

    private sealed record ModelReply(string? Answer, int[]? Citations);
}
