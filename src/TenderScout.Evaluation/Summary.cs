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

    private static string Format(Score score) => Invariant($"{score.Value:F2} of {score.Of}");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
