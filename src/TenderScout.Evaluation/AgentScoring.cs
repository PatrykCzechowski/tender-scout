using System.Text.Json;

namespace TenderScout.Evaluation;

/// <summary>
/// What the agent did in one run, as the results file keeps it: the tool calls in order and two flags. Nothing a
/// tool returned and no answer.
/// </summary>
public sealed record Trajectory(IReadOnlyList<TrajectoryCall> Calls, bool LimitReached, bool Refused);

/// <param name="Arguments">As the model sent them; <see langword="null"/> when they could not be read.</param>
/// <param name="Failed">The tool rejected the call, failed, or does not exist.</param>
public sealed record TrajectoryCall(string Name, JsonElement? Arguments, bool Failed);

/// <summary>How one run of one case scored.</summary>
/// <param name="Expected">The expected calls of the case.</param>
/// <param name="Chosen">Expected calls for which the agent called that tool.</param>
/// <param name="Matched">Chosen calls that did not fail and had the key arguments.</param>
/// <param name="Steps">All tool calls of the run.</param>
/// <param name="LimitHit">Gathering stopped at a limit: round trips, tool calls or time.</param>
/// <param name="Unexpected">Calls that no expected call explains and the case does not allow.</param>
/// <param name="UnknownTools">Unexpected calls to tools that do not exist.</param>
/// <param name="Passed">Every expected call matched, no call was unexpected and no limit was hit.</param>
public sealed record RunScore(
    int Expected,
    int Chosen,
    int Matched,
    int Steps,
    bool LimitHit,
    int Unexpected,
    int UnknownTools,
    int FailedCalls,
    bool Passed);

/// <summary>A count out of a total. With nothing to divide by there is no value: it is shown as n/a.</summary>
public sealed record Share(int Count, int Of)
{
    public double? Value => Of == 0 ? null : (double)Count / Of;
}

/// <summary>One run of one case: its trajectory and score, or the kind of exception that ended it.</summary>
public sealed record CaseRun(string CaseId, int Run, Trajectory? Trajectory, RunScore? Score, string? Error);

/// <summary>The numbers of a set of case runs. A run that ended with an exception counts only as not passed.</summary>
/// <param name="AnyGatheringLimit">Case runs that stopped at any of the three limits.</param>
/// <param name="RunsAtCallCap">Case runs that stopped at a limit with exactly as many calls as the cap: an upper bound.</param>
public sealed record AgentSummary(
    Share ExpectedToolChosen,
    Share ArgumentsMatched,
    Share CasesPassed,
    double? MeanSteps,
    int? LeastSteps,
    int? MostSteps,
    int AnyGatheringLimit,
    int RunsAtCallCap,
    int UnexpectedCalls,
    int UnknownToolCalls,
    int FailedCalls,
    int ErroredRuns);

/// <summary>Scores tool calls against a case; <c>evals/README.md</c> explains the rules.</summary>
public static class AgentScoring
{
    public static RunScore Score(AgentCase @case, Trajectory trajectory)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(trajectory);

        // Each expected call is looked for after the call taken for the one before it.
        var calls = trajectory.Calls;
        var taken = new HashSet<int>();
        var start = 0;
        var matched = 0;
        foreach (var expected in @case.ExpectedCalls)
        {
            var later = Enumerable.Range(start, calls.Count - start).Where(i => calls[i].Name == expected.Tool).ToList();
            var match = later.Where(i => Matches(expected, calls[i])).Take(1).ToList();
            if (later.Count == 0)
            {
                continue;
            }

            // Without a matching call, the first call to the tool counts as chosen with other arguments.
            var index = match.Count > 0 ? match[0] : later[0];
            matched += match.Count;
            taken.Add(index);
            start = index + 1;
        }

        var unexpected = calls.Where((call, i) => !taken.Contains(i) && !@case.AllowedExtraCalls.Contains(call.Name)).ToList();
        return new RunScore(
            @case.ExpectedCalls.Count,
            taken.Count,
            matched,
            calls.Count,
            trajectory.LimitReached,
            unexpected.Count,
            unexpected.Count(call => !AgentCaseSet.Tools.ContainsKey(call.Name)),
            calls.Count(call => call.Failed),
            matched == @case.ExpectedCalls.Count && unexpected.Count == 0 && !trajectory.LimitReached);
    }

    /// <param name="callCap">The agent's limit of tool calls in one run.</param>
    public static AgentSummary Summarize(IReadOnlyCollection<CaseRun> runs, int callCap)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var scores = runs.Select(run => run.Score).OfType<RunScore>().ToList();
        var expected = scores.Sum(s => s.Expected);
        return new AgentSummary(
            new Share(scores.Sum(s => s.Chosen), expected),
            new Share(scores.Sum(s => s.Matched), expected),
            new Share(scores.Count(s => s.Passed), runs.Count),
            scores.Count == 0 ? null : scores.Average(s => s.Steps),
            scores.Count == 0 ? null : scores.Min(s => s.Steps),
            scores.Count == 0 ? null : scores.Max(s => s.Steps),
            scores.Count(s => s.LimitHit),
            scores.Count(s => s.LimitHit && s.Steps == callCap),
            scores.Sum(s => s.Unexpected),
            scores.Sum(s => s.UnknownTools),
            scores.Sum(s => s.FailedCalls),
            runs.Count - scores.Count);
    }

    /// <summary>Whether a call did what the case expects: it did not fail and has every key argument.</summary>
    private static bool Matches(ExpectedCall expected, TrajectoryCall call) =>
        !call.Failed
        && call.Arguments is { ValueKind: JsonValueKind.Object } arguments
        && expected.Arguments.All(e => arguments.TryGetProperty(e.Key, out var actual) && Matches(e.Value, actual));

    private static bool Matches(JsonElement expected, JsonElement actual) => expected.ValueKind switch
    {
        JsonValueKind.Array => expected.EnumerateArray().Any(value => SameValue(value, actual)),
        JsonValueKind.Object => actual.ValueKind == JsonValueKind.String && expected.GetProperty("words").EnumerateArray()
            .All(word => actual.GetString()!.Contains(word.GetString()!, StringComparison.OrdinalIgnoreCase)),
        _ => SameValue(expected, actual),
    };

    /// <summary>Texts are equal without outer spaces and letter case; a number equals the text that spells it.</summary>
    private static bool SameValue(JsonElement expected, JsonElement actual) =>
        Text(expected) is { } text && string.Equals(text, Text(actual), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Trim(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };
}
