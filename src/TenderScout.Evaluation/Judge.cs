using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TenderScout.Infrastructure.Answering;

namespace TenderScout.Evaluation;

/// <summary>
/// Asks a second model whether an answer says only what its cited passages say (faithfulness, LLM-as-a-judge).
/// It uses the chat client of <c>/ask</c> with another model name.
/// </summary>
public static class Judge
{
    internal const string VerdictSchema = """
        {"type":"object","properties":{"faithful":{"type":"boolean"},"reason":{"type":"string"}},"required":["faithful","reason"]}
        """;

    internal const string SystemPrompt = $$"""
        Oceniasz odpowiedzi asystenta na pytania o dokumenty przetargowe.
        Sprawdź, czy każde stwierdzenie w odpowiedzi wynika z podanych fragmentów. Wiedza spoza fragmentów się nie liczy.
        W polu "reason" napisz krótko, dlaczego.
        Zwróć wyłącznie JSON zgodny ze schematem: {{VerdictSchema}}
        """;

    private const int ContextTokens = 8192;

    /// <returns>The verdict, or <see langword="null"/> when the reply is not valid JSON.</returns>
    public static async Task<bool?> IsFaithfulAsync(
        IChatClient chatClient,
        string model,
        string question,
        AskResponse answer,
        CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder()
            .Append("Pytanie: ").AppendLine(question).AppendLine()
            .Append("Odpowiedź: ").AppendLine(answer.Answer).AppendLine()
            .AppendLine("Fragmenty:");
        for (var i = 0; i < answer.Citations.Count; i++)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"[{i + 1}] {answer.Citations[i].Passage}").AppendLine();
        }

        var options = new ChatOptions
        {
            ModelId = model,
            Temperature = 0,
            MaxOutputTokens = QuestionAnswerer.MaxAnswerTokens,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(VerdictSchema).RootElement, "verdict"),
            AdditionalProperties = new() { ["num_ctx"] = ContextTokens },
        };
        var response = await chatClient
            .GetResponseAsync([new(ChatRole.System, SystemPrompt), new(ChatRole.User, prompt.ToString())], options, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<Verdict>(response.Text, JsonSerializerOptions.Web)?.Faithful;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The reason is for a person reading the reply; it is not stored, as it may quote the tender.</summary>
    private sealed record Verdict(bool? Faithful);
}
