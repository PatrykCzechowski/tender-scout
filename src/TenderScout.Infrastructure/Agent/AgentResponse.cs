using System.Text.Json;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.Infrastructure.Agent;

/// <summary>
/// What the agent answered and what it did. The first three fields have the shape of <see cref="AskResponse"/>.
/// </summary>
/// <param name="Refused">No answer rests on a source; <paramref name="Answer"/> is the fixed refusal sentence.</param>
/// <param name="Answer">The answer, or the refusal sentence.</param>
/// <param name="Citations">The passages the answer rests on, as stored.</param>
/// <param name="Notices">The notices the answer rests on, as stored.</param>
/// <param name="LimitReached">Gathering stopped at a limit; the answer uses what was found until then.</param>
/// <param name="ToolCalls">Every tool call of the run, in order.</param>
public sealed record AgentResponse(
    bool Refused,
    string Answer,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<NoticeSummary> Notices,
    bool LimitReached,
    IReadOnlyList<AgentToolCall> ToolCalls);

/// <summary>One tool call the model made.</summary>
/// <param name="Name">The tool the model named, also when no such tool exists.</param>
/// <param name="Arguments">The arguments as the model sent them; <see langword="null"/> when they could not be read.</param>
/// <param name="DurationMs">How long the call took.</param>
/// <param name="Failed">The tool rejected the arguments, does not exist, failed or was abandoned at the time limit.</param>
public sealed record AgentToolCall(string Name, JsonElement? Arguments, long DurationMs, bool Failed);
