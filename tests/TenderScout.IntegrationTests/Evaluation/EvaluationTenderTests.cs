using Microsoft.Extensions.DependencyInjection;
using TenderScout.Evaluation;
using TenderScout.Infrastructure.Answering;
using TenderScout.IntegrationTests.Answering;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>The evaluation limits a question to the tender <c>/ask</c> would choose, on the synthetic set.</summary>
public sealed class EvaluationTenderTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TenderOfTheRecordOrOfItsQuestion_IsTheOneAskChooses()
    {
        var connection = await SyntheticSetDatabase.CreateAsync(database, Ct);
        await using var services = SyntheticSetDatabase.CreateServices(connection, new FakeEmbeddingGenerator(), new FakeChatClient());
        await using var scope = services.CreateAsyncScope();
        var deposit = SyntheticSetDatabase.Questions.Single(q => q.Id == "syn-q05");
        GoldenRecord[] records =
        [
            deposit with { Id = "field", PublicationNumber = "100101-2026" },
            deposit with { Id = "question", Question = deposit.Question + " (100101-2026)" },
            deposit with { Id = "none" },
            deposit with { Id = "several", Question = deposit.Question + " (100101-2026, 100202-2026)" },
            deposit with { Id = "no-documents", Question = deposit.Question + " (100606-2026)" },
        ];

        var rows = await scope.ServiceProvider.GetRequiredService<EvaluationRunner>().RetrieveAsync(records, Ct);

        var answerer = scope.ServiceProvider.GetRequiredService<QuestionAnswerer>();
        foreach (var record in records)
        {
            var chosen = await answerer.ChooseTenderAsync(record.Question, record.PublicationNumber, Ct);
            var row = rows.Single(r => r.Id == record.Id);
            Assert.Equal(chosen.ClearAnswer?.RefusalReason, row.TenderRefusal);
            Assert.Equal(chosen.Source == PublicationNumberSource.Question, row.TenderFromQuestion);
        }

        // The tender's 6 passages against all 30; a question ended with a clear answer is a refusal without ranks.
        Assert.Equal([6, 6, 30, 0, 0], rows.Select(r => r.VectorCandidates));
        Assert.Equal(
            (rows[0].VectorRank, rows[0].TextRank, rows[0].HybridRank, rows[0].Similarity, rows[0].SharedCandidates),
            (rows[1].VectorRank, rows[1].TextRank, rows[1].HybridRank, rows[1].Similarity, rows[1].SharedCandidates));
        Assert.NotNull(rows[0].HybridRank);
        Assert.Equal([null, null, null, RefusalReason.SeveralTenders, RefusalReason.NoDocuments], rows.Select(r => r.TenderRefusal));
        Assert.All(rows.Skip(3), r => Assert.Equal((true, (int?)null), (r.Refused ?? false, r.HybridRank)));
    }
}
