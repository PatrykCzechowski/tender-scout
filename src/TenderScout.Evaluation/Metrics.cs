namespace TenderScout.Evaluation;

/// <summary>
/// What one question scored. Ids and numbers only: the results file must hold no question, quote or passage text.
/// </summary>
public sealed class QuestionResult
{
    public required string Id { get; init; }

    public bool Answerable { get; init; }

    public bool Paraphrase { get; init; }

    /// <summary>One-based rank of the first relevant chunk in the top 10, or <see langword="null"/> if none.</summary>
    public int? VectorRank { get; set; }

    public int? TextRank { get; set; }

    public int? HybridRank { get; set; }

    /// <summary>Cosine similarity of the nearest chunk, the value <c>/ask</c> compares with its threshold.</summary>
    public double Similarity { get; set; }

    /// <summary>A passage given to the model contains a number from the question, so <c>/ask</c> does not refuse early.</summary>
    public bool NumberMatch { get; set; }

    /// <summary><see langword="null"/> until the answer phase runs.</summary>
    public bool? Refused { get; set; }

    public int Citations { get; set; }

    public int RelevantCitations { get; set; }

    /// <summary>The judge's verdict on an answer; <see langword="null"/> when not judged or when its reply was not valid JSON.</summary>
    public bool? Faithful { get; set; }
}

/// <summary>A share or mean, with the number of items it was computed over.</summary>
public sealed record Score(double Value, int Of);

public sealed record RetrievalScore(string Method, string Questions, Score RecallAt5, Score MrrAt10);

public sealed record AnswerScores(
    Score CitationHitRate,
    Score CitationPrecision,
    Score Faithfulness,
    int InvalidJudgeReplies,
    Score CorrectRefusals,
    Score FalseRefusals);

/// <summary>
/// Questions whose nearest chunk is below <paramref name="Threshold"/>, which <c>/ask</c> refuses before calling the model
/// unless a passage has a number from the question; that exception is counted separately.
/// </summary>
public sealed record CalibrationRow(double Threshold, Score FalseRefusals, Score CorrectRefusals);

/// <summary>Metric definitions; <c>evals/README.md</c> explains them.</summary>
public static class Metrics
{
    public const int RecallCutoff = 5;

    public const int MrrCutoff = 10;

    /// <summary>The highest threshold is chosen whose share of refused answerable questions stays at or below this.</summary>
    public const double MaxFalseRefusalRate = 0.10;

    public static readonly IReadOnlyList<double> Thresholds = [0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60];

    public static Score Share(int count, int of) => new(of == 0 ? 0 : (double)count / of, of);

    /// <returns>The one-based rank of the first relevant chunk, or <see langword="null"/> if none is relevant.</returns>
    public static int? FirstRelevantRank(IEnumerable<long> ranking, IReadOnlySet<long> relevant)
    {
        var rank = 0;
        foreach (var id in ranking)
        {
            rank++;
            if (relevant.Contains(id))
            {
                return rank;
            }
        }

        return null;
    }

    public static Score RecallAt5(IReadOnlyCollection<int?> ranks) => Share(ranks.Count(r => r <= RecallCutoff), ranks.Count);

    public static Score MrrAt10(IReadOnlyCollection<int?> ranks) =>
        new(ranks.Count == 0 ? 0 : ranks.Sum(r => r <= MrrCutoff ? 1.0 / r!.Value : 0) / ranks.Count, ranks.Count);

    /// <summary>Recall@5 and MRR@10 of each method over all, paraphrased and literal answerable questions.</summary>
    public static IReadOnlyList<RetrievalScore> Retrieval(IReadOnlyList<QuestionResult> results)
    {
        var answerable = results.Where(r => r.Answerable).ToList();
        (string Name, List<QuestionResult> Results)[] groups =
        [
            ("all", answerable),
            ("paraphrase", [.. answerable.Where(r => r.Paraphrase)]),
            ("literal", [.. answerable.Where(r => !r.Paraphrase)]),
        ];
        (string Name, Func<QuestionResult, int?> Rank)[] methods =
            [("vector", r => r.VectorRank), ("text", r => r.TextRank), ("hybrid", r => r.HybridRank)];

        return [.. from method in methods
                   from g in groups
                   let ranks = g.Results.Select(method.Rank).ToList()
                   select new RetrievalScore(method.Name, g.Name, RecallAt5(ranks), MrrAt10(ranks))];
    }

    public static AnswerScores Answers(IReadOnlyList<QuestionResult> results)
    {
        var answerable = results.Where(r => r.Answerable).ToList();
        var unanswerable = results.Where(r => !r.Answerable).ToList();
        var answeredAnswerable = answerable.Where(r => r.Refused == false).ToList();
        var answered = results.Where(r => r.Refused == false).ToList();

        return new AnswerScores(
            Share(answeredAnswerable.Count(r => r.RelevantCitations > 0), answeredAnswerable.Count),
            Share(answeredAnswerable.Sum(r => r.RelevantCitations), answeredAnswerable.Sum(r => r.Citations)),
            Share(answered.Count(r => r.Faithful == true), answered.Count),
            answered.Count(r => r.Faithful is null),
            Share(unanswerable.Count(r => r.Refused == true), unanswerable.Count),
            Share(answerable.Count(r => r.Refused == true), answerable.Count));
    }

    /// <summary>
    /// By similarity alone. The number exception decides which questions the threshold applies to, not where it
    /// should be; counted in, it would hide the threshold whenever most questions name a year.
    /// </summary>
    public static IReadOnlyList<CalibrationRow> Calibration(IReadOnlyList<QuestionResult> results)
    {
        var answerable = results.Where(r => r.Answerable).ToList();
        var unanswerable = results.Where(r => !r.Answerable).ToList();

        return [.. Thresholds.Select(t => new CalibrationRow(
            t,
            Share(answerable.Count(r => r.Similarity < t), answerable.Count),
            Share(unanswerable.Count(r => r.Similarity < t), unanswerable.Count)))];
    }

    /// <summary>A higher threshold never refuses less, so the highest one within the limit also refuses the most unanswerable questions.</summary>
    public static double? ChooseThreshold(IReadOnlyList<CalibrationRow> rows) =>
        rows.Where(r => r.FalseRefusals.Value <= MaxFalseRefusalRate).Select(r => (double?)r.Threshold).Max();
}
