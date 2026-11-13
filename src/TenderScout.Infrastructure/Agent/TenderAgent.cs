using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.Infrastructure.Agent;

/// <summary>
/// Answers a question that needs several searches, in two phases. First the model calls the tools of
/// <see cref="TenderTools"/>, within fixed limits, and the notices and passages they return are kept. Then one more
/// call answers from a numbered list of those sources, as <c>/ask</c> does, so every source in the response was
/// returned by a tool and none was written by the model. The limits and prompts are recorded in ADR-0006.
/// </summary>
public sealed partial class TenderAgent(
    TenderTools tools,
    IChatClient chatClient,
    IOptions<AgentOptions> options,
    TimeProvider timeProvider,
    ILogger<TenderAgent> logger)
{
    /// <summary>Requests to the model while gathering. The tool calls of the last reply are still run.</summary>
    internal const int MaxRoundTrips = 4;

    internal const int MaxToolCalls = 12;

    internal const int MaxNotices = 25;

    internal const int MaxPassages = 12;

    /// <summary>Holds the largest answering call: 25 notices and 12 passages of up to 1200 characters.</summary>
    internal const int ContextTokens = 12288;

    /// <summary>A gathering reply is a few tool calls or one word.</summary>
    internal const int MaxGatheringTokens = 512;

    /// <summary>What the model is told when a tool fails for a reason that is not its arguments.</summary>
    internal const string ToolFailure = "The tool failed. Call another tool or stop.";

    /// <summary>{0} is today's date in Poland, {1} its day of the week.</summary>
    internal const string GatheringPrompt = """
        Zbierasz informacje potrzebne do odpowiedzi na pytanie o polskie zamówienia publiczne.
        Dzisiaj jest {0} ({1}). Daty i okresy z pytania, na przykład "w przyszłym tygodniu", licz od tej daty.
        Używaj narzędzi, żeby znaleźć ogłoszenia i fragmenty dokumentów przetargowych:
        - Zapytania pisz po polsku, krótko, samymi słowami kluczowymi, bez numeru ogłoszenia.
        - Numer ogłoszenia z pytania, na przykład 123456-2026, przekaż w parametrze publicationNumber.
        - Gdy pytanie opisuje przetarg nazwą lub zamawiającym, najpierw znajdź jego ogłoszenie, a potem szukaj w dokumentach z jego numerem.
        - Używaj tylko parametrów opisanych w narzędziu. Nie podawaj parametru limit, jeśli pytanie nie prosi o konkretną liczbę wyników.
        Nie odpowiadaj na pytanie z własnej wiedzy: odpowiedź powstanie w następnym kroku wyłącznie z wyników narzędzi.
        Gdy masz już to, czego wymaga pytanie, albo narzędzia nic nie znajdują, napisz jedno słowo: GOTOWE.
        """;

    internal const string AnsweringPrompt = $$"""
        Jesteś asystentem, który odpowiada na pytania o zamówienia publiczne.
        Odpowiadaj po polsku i wyłącznie na podstawie podanych źródeł: ogłoszeń i fragmentów dokumentów.
        Każde źródło dotyczy tylko ogłoszenia o numerze podanym przy tym źródle.
        W polu "citations" podaj numery źródeł z nawiasów kwadratowych, na których opierasz odpowiedź, a nie numery stron ani ogłoszeń.
        Jeśli źródła nie zawierają odpowiedzi, zwróć pustą listę "citations".
        Zwróć wyłącznie JSON zgodny ze schematem: {{QuestionAnswerer.AnswerSchema}}
        """;

    internal static readonly TimeSpan GatheringLimit = TimeSpan.FromSeconds(120);

    private static readonly TimeZoneInfo Poland = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly string[] DaysOfWeek = ["niedziela", "poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota"];

    /// <exception cref="Exception">A model could not be reached; see <see cref="QuestionAnswerer.IsUnavailable"/>.</exception>
    public async Task<AgentResponse> AskAsync(string question, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var run = new Run();
        var outcome = "failed";
        try
        {
            await GatherAsync(question, run, cancellationToken).ConfigureAwait(false);
            var response = await AnswerAsync(question, run, cancellationToken).ConfigureAwait(false);
            outcome = response.Refused ? "refused" : "answered";
            return response;
        }
        catch (Exception exception) when (QuestionAnswerer.IsUnavailable(exception, cancellationToken))
        {
            outcome = "unavailable";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            var elapsedMs = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;
            LogRun(
                logger, outcome, run.RoundTrips, run.ToolCalls.Count, run.LimitReached,
                run.Notices.Count + run.Passages.Count, run.SourcesUsed, elapsedMs);
        }
    }

    /// <summary>Lets the model call tools until it stops asking, or until a limit ends the gathering.</summary>
    private async Task GatherAsync(string question, Run run, CancellationToken cancellationToken)
    {
        var today = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), Poland);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, string.Format(CultureInfo.InvariantCulture, GatheringPrompt, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DaysOfWeek[(int)today.DayOfWeek])),
            new(ChatRole.User, question),
        ];
        var chatOptions = new ChatOptions
        {
            ModelId = options.Value.Model,
            Temperature = 0,
            MaxOutputTokens = MaxGatheringTokens,
            Tools = [.. tools.AsFunctions()],
            AdditionalProperties = new() { ["num_ctx"] = ContextTokens },
        };

        // The framework's client runs the loop: it sends each tool result back until the model asks for no tool.
        // Neither wrapper is disposed, because that would dispose the shared chat client.
        var gatherer = new FunctionInvokingChatClient(new RoundTrips(chatClient, run, logger))
        {
            FunctionInvoker = (context, token) => InvokeAsync(context, run, token),
        };

        using var deadline = new CancellationTokenSource(GatheringLimit, timeProvider);
        using var gathering = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await gatherer.GetResponseAsync(messages, chatOptions, gathering.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Out of time: whatever was in flight is abandoned and the answer uses what was found until now.
            run.LimitReached = true;
        }

        if (run.Unavailable is not null)
        {
            ExceptionDispatchInfo.Throw(run.Unavailable);
        }
    }

    /// <summary>Runs one tool call for the model, keeps the sources it returns and records the call.</summary>
    private async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, Run run, CancellationToken cancellationToken)
    {
        if (run.ToolCalls.Count >= MaxToolCalls)
        {
            run.LimitReached = true;
            context.Terminate = true;
            return null;
        }

        var started = timeProvider.GetTimestamp();
        var failed = true;
        Exception? failure = null;
        try
        {
            var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
            run.Keep((JsonElement)result!);
            failed = false;
            return result;
        }
        catch (McpException exception) when (exception.InnerException is { } cause && QuestionAnswerer.IsUnavailable(cause, cancellationToken))
        {
            // The embedding model is down: the run ends as unavailable instead of answering without the documents.
            failure = run.Unavailable = cause;
            context.Terminate = true;
            return ToolFailure;
        }
        catch (McpException exception)
        {
            // The tool's own message names the parameter and the rule, so the model can correct the call.
            return exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception;
            return ToolFailure;
        }
        finally
        {
            run.Record(logger, failure, context.CallContent, (long)timeProvider.GetElapsedTime(started).TotalMilliseconds, failed);
        }
    }

    /// <summary>One call without tools that answers from the numbered sources and says which numbers it used.</summary>
    private async Task<AgentResponse> AnswerAsync(string question, Run run, CancellationToken cancellationToken)
    {
        var notices = run.Notices.Take(MaxNotices).ToList();
        var passages = run.Passages.Take(MaxPassages).ToList();
        run.SourcesUsed = notices.Count + passages.Count;
        var refusal = new AgentResponse(true, QuestionAnswerer.RefusalMessage, [], [], run.LimitReached, run.ToolCalls);
        if (run.SourcesUsed == 0)
        {
            return refusal;
        }

        var chatOptions = QuestionAnswerer.CreateChatOptions();
        chatOptions.ModelId = options.Value.Model;
        chatOptions.AdditionalProperties!["num_ctx"] = ContextTokens;
        var response = await chatClient
            .GetResponseAsync(BuildAnsweringMessages(question, notices, passages), chatOptions, cancellationToken)
            .ConfigureAwait(false);

        var reply = QuestionAnswerer.Parse(response.Text);
        var numbers = (reply?.Citations ?? []).Where(n => n >= 1 && n <= run.SourcesUsed).ToHashSet();
        if (numbers.Count == 0 || string.IsNullOrWhiteSpace(reply?.Answer))
        {
            return refusal;
        }

        // Notices are numbered first, then passages; the response follows that order, not the model's.
        return new AgentResponse(
            false,
            reply.Answer,
            [.. passages.Where((_, i) => numbers.Contains(notices.Count + i + 1)).Select(p => new Citation(p.DocId, p.PublicationNumber, p.Page, p.Text))],
            [.. notices.Where((_, i) => numbers.Contains(i + 1))],
            run.LimitReached,
            run.ToolCalls);
    }

    private static List<ChatMessage> BuildAnsweringMessages(string question, List<NoticeSummary> notices, List<Passage> passages)
    {
        var prompt = new StringBuilder().Append("Pytanie: ").AppendLine(question).AppendLine().AppendLine("Źródła:");
        var number = 0;
        foreach (var n in notices)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"[{++number}] ogłoszenie {n.PublicationNumber}: tytuł: {n.Title ?? "brak"}; zamawiający: {n.BuyerName ?? "brak"}; główny kod CPV: {n.MainCpvCode ?? "brak"}; opublikowano: {n.PublicationDate:yyyy-MM-dd}; termin składania ofert: {n.SubmissionDeadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "brak"}")
                .AppendLine();
        }

        foreach (var p in passages)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"[{++number}] dokument {p.DocId}, ogłoszenie {p.PublicationNumber}, strona {p.Page}")
                .AppendLine(p.Text)
                .AppendLine();
        }

        return [new(ChatRole.System, AnsweringPrompt), new(ChatRole.User, prompt.ToString())];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent tool call {Tool} with {Arguments} took {DurationMs} ms; failed: {Failed}")]
    private static partial void LogToolCall(ILogger logger, Exception? exception, string tool, string arguments, long durationMs, bool failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run {Outcome}: {RoundTrips} round trips, {ToolCalls} tool calls, limit reached: {LimitReached}, sources gathered: {SourcesGathered}, used: {SourcesUsed}, {DurationMs} ms")]
    private static partial void LogRun(ILogger logger, string outcome, int roundTrips, int toolCalls, bool limitReached, int sourcesGathered, int sourcesUsed, long durationMs);

    /// <summary>What one run has gathered and done so far.</summary>
    private sealed class Run
    {
        public List<NoticeSummary> Notices { get; } = [];

        public List<Passage> Passages { get; } = [];

        public List<AgentToolCall> ToolCalls { get; } = [];

        public int RoundTrips { get; set; }

        public bool LimitReached { get; set; }

        public int SourcesUsed { get; set; }

        /// <summary>Why a model could not be reached during a tool call.</summary>
        public Exception? Unavailable { get; set; }

        /// <summary>
        /// Keeps the notices and passages of a tool result, each once, in the order they first came. A notice is the
        /// same when its publication number is; a passage only when document, tender, page and text all are.
        /// </summary>
        public void Keep(JsonElement toolResult)
        {
            var sources = toolResult.Deserialize<Sources>(TenderTools.SerializerOptions)!;
            foreach (var notice in (sources.Notices ?? []).Append(sources.Notice).OfType<NoticeSummary>())
            {
                if (!Notices.Exists(n => n.PublicationNumber == notice.PublicationNumber))
                {
                    Notices.Add(notice);
                }
            }

            Passages.AddRange((sources.Passages ?? []).Except(Passages));
        }

        public void Record(ILogger logger, Exception? failure, FunctionCallContent call, long durationMs, bool failed)
        {
            JsonElement? arguments = call.Arguments is null ? null : JsonSerializer.SerializeToElement(call.Arguments);
            ToolCalls.Add(new AgentToolCall(call.Name, arguments, durationMs, failed));
            var argumentsText = arguments?.GetRawText() ?? "null";
            LogToolCall(logger, failure, call.Name, argumentsText, durationMs, failed);
        }

        /// <summary>The fields of the three tool results that hold sources; a result has one of them.</summary>
        private sealed record Sources(List<NoticeSummary>? Notices, NoticeSummary? Notice, List<Passage>? Passages);
    }

    /// <summary>
    /// Sits between the function-invoking client and the model: counts the requests, stops the gathering at the
    /// round limit, and records the calls to tools that do not exist, which the framework answers by itself.
    /// </summary>
    private sealed class RoundTrips(IChatClient inner, Run run, ILogger logger) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (run.RoundTrips == MaxRoundTrips)
            {
                // The model asked for tools in its last allowed reply. They were run; it is not asked again.
                run.LimitReached = true;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
            }

            run.RoundTrips++;
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

            var unknown = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Where(call => options?.Tools?.Any(tool => tool.Name == call.Name) != true);
            foreach (var call in unknown.Take(MaxToolCalls - run.ToolCalls.Count))
            {
                run.Record(logger, null, call, 0, failed: true);
            }

            return response;
        }
    }
}
