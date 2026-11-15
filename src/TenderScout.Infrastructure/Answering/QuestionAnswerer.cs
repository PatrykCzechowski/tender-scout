using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>The application's own spans; the model calls have theirs from Microsoft.Extensions.AI.</summary>
    private static readonly ActivitySource Telemetry = new("TenderScout");

    /// <param name="publicationNumber">The number field; when it is empty, a tender number in the question is used.</param>
    /// <exception cref="Exception">The embedding or chat model failed; see <see cref="IsUnavailable"/>.</exception>
    public async Task<AskResponse> AskAsync(string question, string? publicationNumber, CancellationToken cancellationToken)
    {
        var tender = await ChooseTenderAsync(question, publicationNumber, cancellationToken).ConfigureAwait(false);
        if (tender.ClearAnswer is { } clearAnswer)
        {
            return clearAnswer;
        }

        var refusal = new AskResponse(true, RefusalMessage, [], tender.PublicationNumber, tender.Source, RefusalReason.NoAnswer);
        var model = embeddingOptions.Value.Model;
        IReadOnlyList<ChunkSearchResult> passages;
        double similarity;

        // One span for finding the sources: the question's vector and both searches. It holds no text of the question.
        using (var retrieval = Telemetry.StartActivity("retrieval"))
        {
            retrieval?.SetTag("gen_ai.operation.name", "retrieval");
            var vector = await generator.GenerateVectorAsync(question, cancellationToken: cancellationToken).ConfigureAwait(false);
            passages = await repository.HybridSearchAsync(question, vector, model, PassageCount, tender.PublicationNumber, cancellationToken)
                .ConfigureAwait(false);

            // Fused scores depend only on rank, so relevance is judged by the cosine similarity of the nearest chunk.
            // No nearest chunk when only the text side found passages, e.g. none embedded yet.
            var nearest = await repository.SearchAsync(vector, model, 1, tender.PublicationNumber, cancellationToken).ConfigureAwait(false);
            similarity = nearest.Count > 0 ? nearest[0].Score : 0;
        }

        if (passages.Count == 0 || (similarity < MinSimilarity && !ContainsNumberFrom(question, passages)))
        {
            return refusal;
        }

        var response = await chatClient.GetResponseAsync(BuildMessages(question, passages), CreateChatOptions(), cancellationToken)
            .ConfigureAwait(false);

        var reply = Parse(response.Text);
        var numbers = (reply?.Citations ?? []).Where(n => n >= 1 && n <= passages.Count).Distinct().ToList();
        if (numbers.Count == 0 || string.IsNullOrWhiteSpace(reply?.Answer))
        {
            LogUnusableReply(logger, response.Text);
            return refusal;
        }

        return new AskResponse(false, reply.Answer, [.. numbers.Select(n => ToCitation(passages[n - 1]))], tender.PublicationNumber, tender.Source);
    }

    /// <summary>
    /// The tender a question is limited to: the number field when it is given, else the one tender number written in
    /// the question, else none. Several numbers in the question, or a tender without a stored passage, end the question
    /// with a clear answer before anything is searched or a model is called.
    /// </summary>
    public async Task<TenderChoice> ChooseTenderAsync(string question, string? publicationNumber, CancellationToken cancellationToken)
    {
        var source = PublicationNumberSource.Field;
        if (publicationNumber is null)
        {
            var named = TenderNumbersIn(question);
            if (named.Count > 1)
            {
                var several = $"W pytaniu są różne numery ogłoszeń: {string.Join(", ", named)}. Podaj jeden numer w polu numeru ogłoszenia.";
                return new TenderChoice(null, null, new AskResponse(true, several, [], RefusalReason: RefusalReason.SeveralTenders));
            }

            if (named.Count == 0)
            {
                return new TenderChoice(null, null, null);
            }

            (publicationNumber, source) = (named[0], PublicationNumberSource.Question);
        }

        return await repository.HasPassagesAsync(publicationNumber, cancellationToken).ConfigureAwait(false)
            ? new TenderChoice(publicationNumber, source, null)
            : new TenderChoice(publicationNumber, source, new AskResponse(
                true, $"Nie mam dokumentów ogłoszenia {publicationNumber}.", [], publicationNumber, source, RefusalReason.NoDocuments));
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

    internal static ModelReply? Parse(string text)
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

    /// <summary>
    /// The different tender numbers written in a text, in their order: 1 to 6 digits, a hyphen and a year from 1900 to
    /// 2099, touching no letter, digit or hyphen, and no dot or slash before it or followed by a digit after it. So a
    /// date, a reference such as <c>ZP.271.5-2026</c> or a phone number is not one.
    /// </summary>
    internal static List<string> TenderNumbersIn(string text) => [.. TenderNumber().Matches(text).Select(m => m.Value).Distinct()];

    private static Citation ToCitation(ChunkSearchResult passage) =>
        new(passage.DocId, passage.PublicationNumber, passage.PageNumber, passage.Text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The chat model gave no valid JSON answer with a valid citation; refusing. Reply: {Reply}")]
    private static partial void LogUnusableReply(ILogger logger, string reply);

    [GeneratedRegex(@"(?<![\w./-])\d{1,6}-(?:19|20)\d{2}(?![\w-]|[./]\d)")]
    private static partial Regex TenderNumber();

    internal sealed record ModelReply(string? Answer, int[]? Citations);
}

/// <summary>The tender a question is limited to, and where its number came from, or the clear answer that ends it.</summary>
public sealed record TenderChoice(string? PublicationNumber, PublicationNumberSource? Source, AskResponse? ClearAnswer);
