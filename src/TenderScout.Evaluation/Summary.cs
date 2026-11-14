using System.Globalization;

namespace TenderScout.Evaluation;

/// <summary>Prints a run as Markdown tables, ready to paste into the README.</summary>
public static class Summary
{
    public static async Task WriteAsync(TextWriter output, EvaluationResults evaluation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(evaluation);

        var lines = new List<string>
        {
            Invariant($"Date {evaluation.Date:yyyy-MM-dd}, commit {evaluation.Commit?[..Math.Min(7, evaluation.Commit.Length)]}, embeddings {evaluation.EmbeddingModel}, answers {evaluation.ChatModel ?? "-"}, judge {evaluation.JudgeModel ?? "-"}"),
            Invariant($"Golden set: {evaluation.GoldenSetSize} questions, labels: {evaluation.AutoLabels} auto, {evaluation.VerifiedLabels} verified"),
            string.Empty,
            "| Search | Questions | Recall@5 | MRR@10 |",
            "| --- | --- | ---: | ---: |",
        };
        lines.AddRange(evaluation.Retrieval.Select(r => Invariant($"| {r.Method} | {r.Questions} ({r.RecallAt5.Of}) | {r.RecallAt5.Value:F2} | {r.MrrAt10.Value:F2} |")));

        lines.AddRange([string.Empty, "| Threshold | False refusals | Correct refusals |", "| ---: | ---: | ---: |"]);
        lines.AddRange(evaluation.Calibration.Select(c => Invariant($"| {c.Threshold:F2} | {Format(c.FalseRefusals)} | {Format(c.CorrectRefusals)} |")));
        lines.Add(Invariant($"Chosen threshold (false refusals at most {Metrics.MaxFalseRefusalRate * 100:F0}%): {evaluation.ChosenThreshold?.ToString("F2", CultureInfo.InvariantCulture) ?? "none"}"));
        lines.Add($"Number exception (no early refusal): answerable {Format(evaluation.AnswerableWithNumberMatch)}, unanswerable {Format(evaluation.UnanswerableWithNumberMatch)}");

        if (evaluation.Answers is { } a)
        {
            lines.AddRange(
            [
                string.Empty,
                "| Answers | Value |",
                "| --- | ---: |",
                $"| Citation hit rate | {Format(a.CitationHitRate)} |",
                $"| Citation precision | {Format(a.CitationPrecision)} |",
                Invariant($"| Faithfulness (invalid judge replies: {a.InvalidJudgeReplies}) | {Format(a.Faithfulness)} |"),
                $"| Correct refusals | {Format(a.CorrectRefusals)} |",
                $"| False refusals | {Format(a.FalseRefusals)} |",
            ]);
        }

        foreach (var line in lines)
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    /// <summary>The per-case table and the overall table of a run of the agent cases.</summary>
    public static async Task WriteAsync(TextWriter output, AgentEvaluationResults results)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(results);

        var o = results.Overall;
        var lines = new List<string>
        {
            Invariant($"Date {results.Date:yyyy-MM-dd}, commit {results.Commit?[..Math.Min(7, results.Commit.Length)]}, agent model {results.AgentModel}, embeddings {results.EmbeddingModel}"),
            Invariant($"{results.Cases} cases, {results.RunsPerCase} runs each, reference date {results.ReferenceDate:yyyy-MM-dd}; limits: {results.MaxRoundTrips} round trips, {results.MaxToolCalls} tool calls, {results.GatheringSeconds} s"),
            string.Empty,
            "| Case | Expected tool chosen | Arguments matched | Steps, least-most | Limit hits | Unexpected calls | Runs passed |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
        };
        foreach (var group in results.CaseRuns.GroupBy(run => run.CaseId))
        {
            var c = AgentScoring.Summarize([.. group], results.MaxToolCalls);
            lines.Add(Invariant($"| {group.Key} | {Format(c.ExpectedToolChosen)} | {Format(c.ArgumentsMatched)} | {c.LeastSteps}-{c.MostSteps} | {c.AnyGatheringLimit} | {c.UnexpectedCalls} | {c.CasesPassed.Count}/{c.CasesPassed.Of} |"));
        }

        lines.AddRange(
        [
            string.Empty,
            "| Number | All runs | Per run, least-most |",
            "| --- | ---: | ---: |",
            $"| Expected tool chosen | {Format(o.ExpectedToolChosen)} | {Spread(results.PerRun, s => s.ExpectedToolChosen)} |",
            $"| Arguments matched | {Format(o.ArgumentsMatched)} | {Spread(results.PerRun, s => s.ArgumentsMatched)} |",
            $"| Cases passed | {Format(o.CasesPassed)} | {Spread(results.PerRun, s => s.CasesPassed)} |",
            Invariant($"| Steps per case run: mean, least, most | {o.MeanSteps:F2}, {o.LeastSteps}, {o.MostSteps} | |"),
            Invariant($"| Any gathering limit (case runs) | {o.AnyGatheringLimit} | |"),
            Invariant($"| Runs at the call cap of {results.MaxToolCalls}, at most | {o.RunsAtCallCap} | |"),
            Invariant($"| Unexpected calls (of them to tools that do not exist) | {o.UnexpectedCalls} ({o.UnknownToolCalls}) | |"),
            Invariant($"| Failed calls | {o.FailedCalls} | |"),
            Invariant($"| Most tool calls in a run (cap {results.MaxToolCalls}) | {o.MostSteps} | |"),
            Invariant($"| Errored case runs | {o.ErroredRuns} | |"),
        ]);

        foreach (var line in lines)
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static string Format(Score score) => Invariant($"{score.Value:F2} of {score.Of}");

    private static string Format(Share share) =>
        share.Value is { } value ? Invariant($"{value:F2} ({share.Count}/{share.Of})") : Invariant($"n/a ({share.Count}/{share.Of})");

    /// <summary>The least and the most of a share over the runs; runs with nothing to divide by are left out.</summary>
    private static string Spread(IReadOnlyList<AgentSummary> perRun, Func<AgentSummary, Share> share)
    {
        var values = perRun.Select(run => share(run).Value).OfType<double>().ToList();
        return values.Count == 0 ? "n/a" : Invariant($"{values.Min():F2}-{values.Max():F2}");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
