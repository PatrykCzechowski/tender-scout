using TenderScout.Evaluation;
using TenderScout.Infrastructure.Answering;

namespace TenderScout.UnitTests.Evaluation;

public sealed class SummaryTests
{
    [Fact]
    public async Task Diagnostics_NameTheLostQuestionsWithTheirCandidates_AndTheListsCutShort()
    {
        QuestionResult[] rows =
        [
            new() { Id = "q-lost", Answerable = true, VectorRank = 2, HybridRank = 7, SharedCandidates = 6, VectorCandidates = 40, VectorCandidateRank = 2, TextCandidates = 40 },
            new() { Id = "q-kept", Answerable = true, VectorRank = 1, HybridRank = 1, SharedCandidates = 9, VectorCandidates = 40, VectorCandidateRank = 1, TextCandidates = 12, TextCandidateRank = 3 },
            new() { Id = "q-short", Answerable = true, SharedCandidates = 2, VectorCandidates = 4, TextCandidates = 3, TenderChunksWithVector = 9 },
            new() { Id = "q-small", Answerable = false, SharedCandidates = 3, VectorCandidates = 3, TextCandidates = 5, TenderChunksWithVector = 3 },
        ];
        using var output = new StringWriter();

        await Summary.WriteAsync(output, new EvaluationResults(
            DateTimeOffset.UnixEpoch, "abc1234", "bge-m3", null, null, rows.Length, 0, rows.Length,
            Metrics.Retrieval(rows), Metrics.Calibration(rows), null, new Score(0, 0), new Score(0, 0), null, rows));

        var text = output.ToString();
        Assert.Contains("Questions with at least 5 candidates in both lists: 2 of 4", text, StringComparison.Ordinal);
        Assert.Contains("In the vector top 5, not in the hybrid top 5: q-lost (shared 6, vector candidate rank 2, full-text candidate rank none)", text, StringComparison.Ordinal);
        Assert.Contains("Vector lists cut short by the index: q-short\n", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenderLines_CountTheTendersFromTheQuestion_AndNameTheQuestionsNotSearched()
    {
        QuestionResult[] rows =
        [
            new() { Id = "q-field", Answerable = true },
            new() { Id = "q-text", Answerable = true, TenderFromQuestion = true },
            new() { Id = "q-two", Answerable = true, TenderRefusal = RefusalReason.SeveralTenders, Refused = true },
            new() { Id = "q-empty", Answerable = false, TenderFromQuestion = true, TenderRefusal = RefusalReason.NoDocuments, Refused = true },
        ];
        using var output = new StringWriter();

        await Summary.WriteAsync(output, new EvaluationResults(
            DateTimeOffset.UnixEpoch, "abc1234", "bge-m3", null, null, rows.Length, 0, rows.Length,
            Metrics.Retrieval(rows), Metrics.Calibration(rows), null, new Score(0, 0), new Score(0, 0), null, rows));

        var text = output.ToString();
        Assert.Contains("Tender taken from the question text: 2 of 4", text, StringComparison.Ordinal);
        Assert.Contains("Not searched, a clear refusal of /ask: q-two, q-empty", text, StringComparison.Ordinal);
    }
}
