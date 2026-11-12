using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Evaluation;

/// <summary>Everything one run measured, as written to the results file.</summary>
public sealed record EvaluationResults(
    DateTimeOffset Date,
    string? Commit,
    string EmbeddingModel,
    string? ChatModel,
    string? JudgeModel,
    int GoldenSetSize,
    int AutoLabels,
    int VerifiedLabels,
    IReadOnlyList<RetrievalScore> Retrieval,
    IReadOnlyList<CalibrationRow> Calibration,
    double? ChosenThreshold,
    Score AnswerableWithNumberMatch,
    Score UnanswerableWithNumberMatch,
    AnswerScores? Answers,
    IReadOnlyList<QuestionResult> Questions);

/// <summary>
/// Measures retrieval and <c>/ask</c> answers on a golden set: the labels are checked, the three searches ranked,
/// the results written, then every question answered and, in a separate phase, every answer judged.
/// </summary>
public sealed class EvaluationRunner(
    TenderScoutDbContext dbContext,
    ChunkEmbeddingRepository repository,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    QuestionAnswerer answerer,
    IChatClient chatClient,
    IOptions<EmbeddingOptions> embeddingOptions,
    IOptions<ChatModelOptions> chatOptions,
    TimeProvider timeProvider)
{
    private const int Failed = 1;
    private const int Succeeded = 0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web) { WriteIndented = true };

    /// <param name="retrievalOnly">Skips the answer and judge phases, so no chat model is needed.</param>
    /// <returns>The exit code of the command.</returns>
    public async Task<int> RunAsync(
        string goldenSetPath,
        string judgeModel,
        bool retrievalOnly,
        string outputDirectory,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        var records = GoldenRecord.Load(goldenSetPath);
        IReadOnlyList<QuestionResult> results;
        try
        {
            results = await RetrieveAsync(records, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            await output.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return Failed;
        }

        var file = Path.Combine(outputDirectory, $"{timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(outputDirectory);
        var evaluation = Summarize(records, results, retrievalOnly ? null : judgeModel, answers: null);
        await WriteAsync(file, evaluation, cancellationToken).ConfigureAwait(false);

        if (!retrievalOnly)
        {
            try
            {
                await AnswerAndJudgeAsync(records, results, judgeModel, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (QuestionAnswerer.IsUnavailable(ex, cancellationToken))
            {
                await output.WriteLineAsync($"A chat model is unavailable ({ex.Message}); retrieval results are in {file}.").ConfigureAwait(false);
                return Failed;
            }

            evaluation = Summarize(records, results, judgeModel, Metrics.Answers(results));
            await WriteAsync(file, evaluation, cancellationToken).ConfigureAwait(false);
        }

        await Summary.WriteAsync(output, evaluation).ConfigureAwait(false);
        await output.WriteLineAsync($"Results: {file}").ConfigureAwait(false);
        return Succeeded;
    }

    /// <summary>
    /// Ranks the chunks of vector, full-text and hybrid search for every question, as <c>/ask</c> would search,
    /// within the question's tender if it names one. Questions with a stored vector are not sent to the embedding model.
    /// </summary>
    /// <exception cref="InvalidDataException">A quote is in no chunk of its document near its page; names the records.</exception>
    public async Task<IReadOnlyList<QuestionResult>> RetrieveAsync(IReadOnlyList<GoldenRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        var relevant = await FindRelevantChunksAsync(records, cancellationToken).ConfigureAwait(false);
        var broken = records.Where(r => r.Answerable && !relevant[r.Id].Any(c => c.Page == r.Page || c.Page == r.Page - 1)).ToList();
        if (broken.Count > 0)
        {
            throw new InvalidDataException(
                $"No chunk of the document contains the quote on its page or the one before: {string.Join(", ", broken.Select(r => r.Id))}");
        }

        var model = embeddingOptions.Value.Model;
        var results = new List<QuestionResult>();
        foreach (var record in records)
        {
            var vector = record.Vector
                ?? (await generator.GenerateVectorAsync(record.Question, cancellationToken: cancellationToken).ConfigureAwait(false)).ToArray();
            var tender = record.PublicationNumber;
            var vectorRanking = await repository.SearchAsync(vector, model, Metrics.MrrCutoff, tender, cancellationToken).ConfigureAwait(false);
            var textRanking = await repository.TextSearchAsync(record.Question, tender, cancellationToken).ConfigureAwait(false);
            var hybridRanking = await repository.HybridSearchAsync(record.Question, vector, model, Metrics.MrrCutoff, tender, cancellationToken)
                .ConfigureAwait(false);

            var relevantIds = relevant.GetValueOrDefault(record.Id, []).Select(c => c.Id).ToHashSet();
            results.Add(new QuestionResult
            {
                Id = record.Id,
                Answerable = record.Answerable,
                Paraphrase = record.Paraphrase,
                VectorRank = Metrics.FirstRelevantRank(vectorRanking.Select(c => c.ChunkId), relevantIds),
                TextRank = Metrics.FirstRelevantRank(textRanking.Take(Metrics.MrrCutoff), relevantIds),
                HybridRank = Metrics.FirstRelevantRank(hybridRanking.Select(c => c.ChunkId), relevantIds),
                Similarity = vectorRanking.Count > 0 ? vectorRanking[0].Score : 0,
                NumberMatch = QuestionAnswerer.ContainsNumberFrom(record.Question, [.. hybridRanking.Take(QuestionAnswerer.PassageCount)]),
            });
        }

        return results;
    }

    /// <summary>The chunks of each answerable record's document that contain its quote.</summary>
    private async Task<Dictionary<string, List<(long Id, int Page)>>> FindRelevantChunksAsync(
        IReadOnlyList<GoldenRecord> records,
        CancellationToken cancellationToken)
    {
        var answerable = records.Where(r => r.Answerable).ToList();
        var docIds = answerable.Select(r => r.DocId).Distinct().ToList();
        var chunks = await dbContext.DocumentChunks
            .AsNoTracking()
            .Join(dbContext.Documents.Where(d => docIds.Contains(d.DocId)), c => c.DocumentId, d => d.Id, (c, d) => new { c.Id, d.DocId, c.PageNumber, c.Text })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return answerable.ToDictionary(
            r => r.Id,
            r => chunks.Where(c => r.IsAnsweredBy(c.DocId, c.Text)).Select(c => (c.Id, c.PageNumber)).ToList());
    }

    /// <summary>All answers come first, so the answer model and the judge are never loaded at the same time.</summary>
    private async Task AnswerAndJudgeAsync(
        IReadOnlyList<GoldenRecord> records,
        IReadOnlyList<QuestionResult> results,
        string judgeModel,
        CancellationToken cancellationToken)
    {
        var answers = new List<(GoldenRecord Record, QuestionResult Result, AskResponse Answer)>();
        for (var i = 0; i < records.Count; i++)
        {
            var answer = await answerer.AskAsync(records[i].Question, records[i].PublicationNumber, cancellationToken).ConfigureAwait(false);
            results[i].Refused = answer.Refused;
            results[i].Citations = answer.Citations.Count;
            results[i].RelevantCitations = answer.Citations.Count(c => records[i].IsAnsweredBy(c.DocId, c.Passage));
            answers.Add((records[i], results[i], answer));
        }

        foreach (var (record, result, answer) in answers.Where(a => !a.Answer.Refused))
        {
            result.Faithful = await Judge.IsFaithfulAsync(chatClient, judgeModel, record.Question, answer, cancellationToken).ConfigureAwait(false);
        }
    }

    private EvaluationResults Summarize(
        IReadOnlyList<GoldenRecord> records,
        IReadOnlyList<QuestionResult> results,
        string? judgeModel,
        AnswerScores? answers)
    {
        var calibration = Metrics.Calibration(results);
        return new EvaluationResults(
            timeProvider.GetUtcNow(),
            typeof(EvaluationRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1),
            embeddingOptions.Value.Model,
            judgeModel is null ? null : chatOptions.Value.Model,
            judgeModel,
            records.Count,
            records.Count(r => r.LabelSource == "auto"),
            records.Count(r => r.LabelSource == "verified"),
            Metrics.Retrieval(results),
            calibration,
            Metrics.ChooseThreshold(calibration),
            Metrics.Share(results.Count(r => r.Answerable && r.NumberMatch), results.Count(r => r.Answerable)),
            Metrics.Share(results.Count(r => !r.Answerable && r.NumberMatch), results.Count(r => !r.Answerable)),
            answers,
            results);
    }

    private static async Task WriteAsync(string file, EvaluationResults evaluation, CancellationToken cancellationToken)
    {
        var stream = File.Create(file);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, evaluation, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
    }
}
