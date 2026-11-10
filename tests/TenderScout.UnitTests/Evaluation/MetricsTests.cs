using TenderScout.Evaluation;

namespace TenderScout.UnitTests.Evaluation;

public sealed class MetricsTests
{
    [Fact]
    public void RecallAndMrr_OfRanks1_3_AndNone_Are2Of3_And4Of9()
    {
        int?[] ranks = [1, 3, null];

        Assert.Equal(new Score(2.0 / 3, 3), Metrics.RecallAt5(ranks));
        Assert.Equal((1 + (1.0 / 3)) / 3, Metrics.MrrAt10(ranks).Value, 10);
    }

    [Fact]
    public void Rank8_CountsForMrrButNotForRecall()
    {
        int?[] ranks = [1, 3, 8, null];

        Assert.Equal(0.5, Metrics.RecallAt5(ranks).Value);
        Assert.Equal(35.0 / 96, Metrics.MrrAt10(ranks).Value, 10);
    }

    [Fact]
    public void FirstRelevantRank_IsTheBestOfSeveralRelevantChunks()
    {
        Assert.Equal(2, Metrics.FirstRelevantRank([10, 20, 30, 40], new HashSet<long> { 20, 40 }));
        Assert.Null(Metrics.FirstRelevantRank([10, 20], new HashSet<long> { 99 }));
    }

    [Fact]
    public void Retrieval_SplitsParaphrasedAndLiteralQuestions_AndSkipsUnanswerableOnes()
    {
        QuestionResult[] results =
        [
            new() { Id = "p1", Answerable = true, Paraphrase = true, HybridRank = 2 },
            new() { Id = "p2", Answerable = true, Paraphrase = true },
            new() { Id = "l1", Answerable = true, HybridRank = 1 },
            new() { Id = "l2", Answerable = true, HybridRank = 4 },
            new() { Id = "none", HybridRank = 1 },
        ];

        var hybrid = Metrics.Retrieval(results).Where(s => s.Method == "hybrid").ToDictionary(s => s.Questions, s => s.RecallAt5);

        Assert.Equal(new Score(0.75, 4), hybrid["all"]);
        Assert.Equal(new Score(0.5, 2), hybrid["paraphrase"]);
        Assert.Equal(new Score(1, 2), hybrid["literal"]);
    }

    [Fact]
    public void Answers_CountCitationsOverAnsweredQuestions_AndRefusalsOverEachKind()
    {
        QuestionResult[] results =
        [
            new() { Id = "a1", Answerable = true, Refused = false, Citations = 2, RelevantCitations = 1, Faithful = true },
            new() { Id = "a2", Answerable = true, Refused = false, Citations = 2, RelevantCitations = 2, Faithful = false },
            new() { Id = "a3", Answerable = true, Refused = false, Citations = 1, RelevantCitations = 0 },
            new() { Id = "a4", Answerable = true, Refused = true },
            new() { Id = "n1", Refused = true },
            new() { Id = "n2", Refused = false, Citations = 1, Faithful = true },
        ];

        var scores = Metrics.Answers(results);

        Assert.Equal(new Score(2.0 / 3, 3), scores.CitationHitRate);
        Assert.Equal(new Score(3.0 / 5, 5), scores.CitationPrecision);
        Assert.Equal(new Score(0.5, 4), scores.Faithfulness);
        Assert.Equal(1, scores.InvalidJudgeReplies);
        Assert.Equal(new Score(0.5, 2), scores.CorrectRefusals);
        Assert.Equal(new Score(0.25, 4), scores.FalseRefusals);
    }

    [Fact]
    public void Calibration_ChoosesTheHighestThresholdWithAtMostTenPercentFalseRefusals_BySimilarityAlone()
    {
        double[] answerable = [0.46, 0.48, 0.51, 0.55, 0.62, 0.65, 0.70, 0.72, 0.75, 0.80];
        double[] unanswerable = [0.32, 0.41, 0.47, 0.58];
        QuestionResult[] results =
        [
            .. answerable.Select((s, i) => new QuestionResult { Id = $"a{i}", Answerable = true, Similarity = s }),
            .. unanswerable.Select((s, i) => new QuestionResult { Id = $"n{i}", Similarity = s, NumberMatch = true }),
        ];

        var rows = Metrics.Calibration(results);

        Assert.Equal(Metrics.Thresholds, rows.Select(r => r.Threshold));
        var at045 = rows.Single(r => r.Threshold == 0.45);
        Assert.Equal((new Score(0, 10), new Score(0.5, 4)), (at045.FalseRefusals, at045.CorrectRefusals));
        Assert.Equal(new Score(0.2, 10), rows.Single(r => r.Threshold == 0.50).FalseRefusals);
        Assert.Equal(0.45, Metrics.ChooseThreshold(rows));
    }

    [Fact]
    public void ChooseThreshold_IsNull_WhenEveryThresholdRefusesTooManyAnswerableQuestions()
    {
        QuestionResult[] results = [new() { Id = "a", Answerable = true, Similarity = 0.1 }];

        Assert.Null(Metrics.ChooseThreshold(Metrics.Calibration(results)));
    }

    [Fact]
    public void EmptyGroup_ScoresZeroOfZero()
    {
        Assert.Equal(new Score(0, 0), Metrics.RecallAt5([]));
        Assert.Equal(new Score(0, 0), Metrics.MrrAt10([]));
    }
}
