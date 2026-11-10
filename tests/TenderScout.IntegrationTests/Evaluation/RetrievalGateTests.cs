using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TenderScout.Evaluation;
using TenderScout.IntegrationTests.Answering;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>
/// The CI gate: 30 invented passages and 20 invented questions with stored bge-m3 vectors, so no model runs.
/// Each threshold is the value measured on 2026-11-10 with bge-m3 minus 0.05, rounded down to 0.05: a small
/// deliberate change passes, a broken fusion or text query does not. The README shows the same numbers.
/// </summary>
public sealed class RetrievalGateTests(PostgresFixture database)
{
    private static readonly Dictionary<string, (double RecallAt5, double MrrAt10)> Thresholds = new()
    {
        ["vector"] = (0.90, 0.50),
        ["text"] = (0.55, 0.35),
        ["hybrid"] = (0.90, 0.45),
    };

    [Fact]
    public async Task EverySearch_StaysAtOrAboveItsRecordedThresholds()
    {
        var connection = await SyntheticSetDatabase.CreateAsync(database, TestContext.Current.CancellationToken);
        var generator = new FakeEmbeddingGenerator();
        await using var services = SyntheticSetDatabase.CreateServices(connection, generator, new FakeChatClient());
        await using var scope = services.CreateAsyncScope();

        var results = await scope.ServiceProvider.GetRequiredService<EvaluationRunner>()
            .RetrieveAsync(SyntheticSetDatabase.Questions, TestContext.Current.CancellationToken);

        var scores = Metrics.Retrieval(results).Where(s => s.Questions == "all").ToList();
        var failures = scores
            .SelectMany(s => new[]
            {
                (Name: $"{s.Method} recall@5", s.RecallAt5.Value, Threshold: Thresholds[s.Method].RecallAt5),
                (Name: $"{s.Method} MRR@10", s.MrrAt10.Value, Threshold: Thresholds[s.Method].MrrAt10),
            })
            .Where(m => m.Value < m.Threshold)
            .Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Name} is {m.Value:F2}, below its threshold {m.Threshold:F2}"))
            .ToList();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(
            Environment.NewLine,
            scores.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Method}: recall@5 {s.RecallAt5.Value:F4}, MRR@10 {s.MrrAt10.Value:F4}"))));

        Assert.Equal(20, scores[0].RecallAt5.Of);
        Assert.Empty(generator.Calls);
        Assert.Empty(failures);
    }
}
