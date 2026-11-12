using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenderScout.Evaluation;
using TenderScout.IntegrationTests.Answering;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>
/// The command over the synthetic set with fake models. The fake generator returns each question's stored vector,
/// and the fake chat model's one reply is both a valid answer citing passage 1 and a valid verdict.
/// </summary>
public sealed class EvaluationRunnerTests(PostgresFixture database) : IDisposable
{
    private const string JudgeModel = "judge-model";
    private const string Unanswerable = """{"id": "none-1", "question": "Jaka będzie jutro pogoda nad morzem?", "answerable": false, "paraphrase": false, "label_source": "auto"}""";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("tender-scout-eval-");
    private readonly FakeEmbeddingGenerator _generator = new();
    private readonly FakeChatClient _chat = new() { Reply = """{"answer": "Odpowiedź.", "citations": [1], "faithful": true, "reason": "Zgodne."}""" };
    private readonly StringWriter _output = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string OutputFolder => Path.Combine(_folder.FullName, "eval");

    [Fact]
    public async Task Run_AnswersEveryQuestionBeforeJudging_AndWritesIdsAndNumbersOnly()
    {
        var questions = SyntheticSetDatabase.Questions.Take(3).ToList();

        var exitCode = await RunAsync(retrievalOnly: false, [.. questions.Select(ToJson), Unanswerable]);

        Assert.Equal(0, exitCode);
        var calls = _chat.Calls.Select(c => c.Options!.ModelId).ToList();
        Assert.Equal([null, null, null, JudgeModel, JudgeModel, JudgeModel], calls);
        Assert.Equal(0, _chat.Calls[^1].Options!.Temperature);

        var file = Assert.Single(Directory.GetFiles(OutputFolder));
        var json = await File.ReadAllTextAsync(file, Ct);
        var results = JsonSerializer.Deserialize<EvaluationResults>(json, JsonSerializerOptions.Web)!;
        Assert.Equal((4, 3, 1), (results.GoldenSetSize, results.VerifiedLabels, results.AutoLabels));
        Assert.Equal((SyntheticSetDatabase.AnswerModel, JudgeModel), (results.ChatModel, results.JudgeModel));
        Assert.Equal(new Score(1, 1), results.Answers!.CorrectRefusals);
        Assert.Equal(new Score(1, 3), results.Answers.Faithfulness);
        Assert.Equal(["syn-q01", "syn-q02", "syn-q03", "none-1"], results.Questions.Select(q => q.Id));
        Assert.All(questions, q => Assert.DoesNotContain(q.Question, json, StringComparison.Ordinal));
        Assert.All(questions, q => Assert.DoesNotContain(q.Quote!, json, StringComparison.Ordinal));
        Assert.DoesNotContain("pogoda", json, StringComparison.Ordinal);
        Assert.Contains("| hybrid | all (3) |", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetrievalOnly_CallsNoChatModel()
    {
        var exitCode = await RunAsync(retrievalOnly: true, [.. SyntheticSetDatabase.Questions.Take(2).Select(ToJson), Unanswerable]);

        Assert.Equal(0, exitCode);
        Assert.Empty(_chat.Calls);
        var results = JsonSerializer.Deserialize<EvaluationResults>(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(OutputFolder)), Ct), JsonSerializerOptions.Web)!;
        Assert.Null(results.Answers);
        Assert.Equal(7, results.Calibration.Count);
    }

    [Fact]
    public async Task UnavailableChatModel_FailsAfterWritingTheRetrievalResults()
    {
        _chat.Failure = new HttpRequestException("Connection refused");

        var exitCode = await RunAsync(retrievalOnly: false, [.. SyntheticSetDatabase.Questions.Take(2).Select(ToJson)]);

        Assert.Equal(1, exitCode);
        Assert.Contains("A chat model is unavailable (Connection refused)", _output.ToString(), StringComparison.Ordinal);
        var results = JsonSerializer.Deserialize<EvaluationResults>(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(OutputFolder)), Ct), JsonSerializerOptions.Web)!;
        Assert.Equal(2, results.Retrieval.Single(r => r.Method == "hybrid" && r.Questions == "all").RecallAt5.Of);
        Assert.Null(results.Answers);
    }

    [Fact]
    public async Task QuoteMissingFromItsDocumentOrPage_StopsBeforeMeasuring_NamingTheRecords()
    {
        var first = SyntheticSetDatabase.Questions[0];

        var exitCode = await RunAsync(
            retrievalOnly: true,
            [
                ToJson(first),
                ToJson(first with { Id = "wrong-quote", Quote = "Tego zdania nie ma w żadnym fragmencie." }),
                ToJson(first with { Id = "wrong-page", Page = first.Page + 5 }),
            ]);

        Assert.Equal(1, exitCode);
        Assert.Contains("wrong-quote, wrong-page", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(first.Id, _output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(OutputFolder));
        Assert.Empty(_generator.Calls);
    }

    public void Dispose()
    {
        _output.Dispose();
        _folder.Delete(recursive: true);
    }

    private static string ToJson(GoldenRecord record) =>
        JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    private async Task<int> RunAsync(bool retrievalOnly, string[] goldenLines)
    {
        var goldenSetPath = Path.Combine(_folder.FullName, "golden.jsonl");
        await File.WriteAllLinesAsync(goldenSetPath, goldenLines, Ct);

        // /ask embeds the question again; it gets the stored vector, or a random one for a question without it.
        var stored = SyntheticSetDatabase.Questions.ToDictionary(q => q.Question, q => q.Vector!);
        _generator.Transform = vectors => [.. _generator.Calls[^1].Select((text, i) => stored.GetValueOrDefault(text, vectors[i]))];

        var connection = await SyntheticSetDatabase.CreateAsync(database, Ct);
        await using var services = SyntheticSetDatabase.CreateServices(connection, _generator, _chat);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EvaluationRunner>()
            .RunAsync(goldenSetPath, JudgeModel, retrievalOnly, OutputFolder, _output, Ct);
    }
}
