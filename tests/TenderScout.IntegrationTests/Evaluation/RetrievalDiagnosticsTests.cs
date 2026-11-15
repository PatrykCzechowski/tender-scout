using Microsoft.Extensions.DependencyInjection;
using TenderScout.Evaluation;
using TenderScout.Infrastructure.Persistence;
using TenderScout.IntegrationTests.Answering;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>The candidate lists behind each hybrid result, read on the synthetic set with its stored vectors.</summary>
public sealed class RetrievalDiagnosticsTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryRow_CountsAndRanksTheCandidatesThatHybridSearchFused()
    {
        var connection = await SyntheticSetDatabase.CreateAsync(database, Ct);
        await using var services = SyntheticSetDatabase.CreateServices(connection, new FakeEmbeddingGenerator(), new FakeChatClient());
        await using var scope = services.CreateAsyncScope();
        var questions = SyntheticSetDatabase.Questions;

        var all = await scope.ServiceProvider.GetRequiredService<EvaluationRunner>().RetrieveAsync(questions, Ct);
        var filtered = await scope.ServiceProvider.GetRequiredService<EvaluationRunner>()
            .RetrieveAsync([.. questions.Select(q => q with { PublicationNumber = "100101-2026" })], Ct);

        // Without a tender every one of the 30 chunks has a vector; within 100101-2026 there are 6.
        Assert.All(all, row => Assert.Equal((30, null), (row.VectorCandidates, row.TenderChunksWithVector)));
        Assert.All(filtered, row => Assert.Equal((6, 6), (row.VectorCandidates, row.TenderChunksWithVector)));
        Assert.All(all.Concat(filtered), row =>
        {
            Assert.InRange(row.SharedCandidates, 0, Math.Min(row.VectorCandidates, row.TextCandidates));
            Assert.Equal(row.VectorRank, row.VectorCandidateRank <= Metrics.MrrCutoff ? row.VectorCandidateRank : null);
            Assert.Equal(row.TextRank, row.TextCandidateRank <= Metrics.MrrCutoff ? row.TextCandidateRank : null);
        });
        Assert.Contains(all, row => row.SharedCandidates > 0 && row.SharedCandidates < row.TextCandidates + row.VectorCandidates);
    }

    [Fact]
    public async Task RelevantChunkTwelfthAmongTheCandidates_HasCandidateRank12_AndNoTop10Rank()
    {
        var connection = await SyntheticSetDatabase.CreateAsync(database, Ct);
        await using var services = SyntheticSetDatabase.CreateServices(connection, new FakeEmbeddingGenerator(), new FakeChatClient());
        await using var scope = services.CreateAsyncScope();
        var question = SyntheticSetDatabase.Questions[0];
        var candidates = await scope.ServiceProvider.GetRequiredService<ChunkEmbeddingRepository>()
            .SearchAsync(question.Vector, "bge-m3", ChunkEmbeddingRepository.MaxSearchResults, cancellationToken: Ct);
        var twelfth = candidates[11];

        var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<EvaluationRunner>().RetrieveAsync(
            [question with { DocId = twelfth.DocId, Page = twelfth.PageNumber, Quote = twelfth.Text }], Ct));

        Assert.Equal(12, row.VectorCandidateRank);
        Assert.Null(row.VectorRank);
    }
}
