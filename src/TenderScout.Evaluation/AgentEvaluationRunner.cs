using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Agent;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.Evaluation;

/// <summary>Everything one run of the agent cases recorded, as written to the results file.</summary>
/// <param name="ReferenceDate">The date the agent was told is today.</param>
/// <param name="Complete">False when a model could not be reached and the run stopped early.</param>
/// <param name="PerRun">The numbers of the first runs of all cases, of the second runs, and so on.</param>
public sealed record AgentEvaluationResults(
    DateTimeOffset Date,
    string? Commit,
    string AgentModel,
    string EmbeddingModel,
    DateOnly ReferenceDate,
    int MaxRoundTrips,
    int MaxToolCalls,
    int GatheringSeconds,
    int Cases,
    int RunsPerCase,
    bool Complete,
    AgentSummary Overall,
    IReadOnlyList<AgentSummary> PerRun,
    IReadOnlyList<CaseRun> CaseRuns);

/// <summary>
/// Measures which tools the agent calls: loads the invented notices and passages into an empty database, asks the
/// agent every case several times, and scores the tool calls of each run against the case. Answers are not judged.
/// </summary>
public sealed class AgentEvaluationRunner(
    TenderScoutDbContext dbContext,
    TenderTools tools,
    IChatClient chatClient,
    IOptions<AgentOptions> agentOptions,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<TenderAgent> agentLogger,
    TimeProvider timeProvider)
{
    /// <summary>The model's calls can differ between runs of the same question.</summary>
    public const int RunsPerCase = 3;

    private const int Failed = 1;
    private const int Succeeded = 0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web) { WriteIndented = true };

    /// <param name="passagesFile">The synthetic passages with their stored vectors.</param>
    /// <returns>The exit code of the command.</returns>
    public async Task<int> RunAsync(
        string caseFolder,
        string passagesFile,
        string outputDirectory,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (string.IsNullOrWhiteSpace(agentOptions.Value.Model))
        {
            await output.WriteLineAsync("Set Agent:Model in user secrets to a chat model that can call tools.").ConfigureAwait(false);
            return Failed;
        }

        AgentCaseSet caseSet;
        try
        {
            caseSet = AgentCaseSet.Load(caseFolder);
        }
        catch (InvalidDataException exception)
        {
            await output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return Failed;
        }

        if (!await LoadDataAsync(caseSet, passagesFile, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync("The database already holds notices or documents. The agent cases need an empty database of their own.").ConfigureAwait(false);
            return Failed;
        }

        var connection = dbContext.Database.GetDbConnection();
        await output.WriteLineAsync($"Agent model {agentOptions.Value.Model}, embedding model {embeddingOptions.Value.Model}, database {connection.Database} on {connection.DataSource}").ConfigureAwait(false);

        var runs = new List<CaseRun>();
        string? unavailable = null;
        try
        {
            await RunCasesAsync(caseSet, runs, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (QuestionAnswerer.IsUnavailable(exception, cancellationToken))
        {
            unavailable = exception.Message;
        }

        var results = Summarize(caseSet, runs, complete: unavailable is null);
        var file = await WriteAsync(results, outputDirectory, cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            await output.WriteLineAsync($"A model is unavailable ({unavailable}). The run is incomplete; what was recorded is in {file}.").ConfigureAwait(false);
            return Failed;
        }

        await Summary.WriteAsync(output, results).ConfigureAwait(false);
        await output.WriteLineAsync($"Results: {file}").ConfigureAwait(false);
        return results.Overall.ErroredRuns == 0 ? Succeeded : Failed;
    }

    /// <returns>The path of the results file; its name cannot be taken for a file of the search evaluation.</returns>
    private static async Task<string> WriteAsync(AgentEvaluationResults results, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var file = Path.Combine(outputDirectory, $"agent-{results.Date:yyyyMMdd-HHmmss}.json");
        var stream = File.Create(file);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, results, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        return file;
    }

    /// <returns>False, with nothing written, when the database already holds a notice or a document.</returns>
    private async Task<bool> LoadDataAsync(AgentCaseSet caseSet, string passagesFile, CancellationToken cancellationToken)
    {
        // Asked before the schema is created, so a database with data is not touched at all.
        var hasSchema = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any();
        if (hasSchema
            && (await dbContext.Notices.AnyAsync(cancellationToken).ConfigureAwait(false)
                || await dbContext.Documents.AnyAsync(cancellationToken).ConfigureAwait(false)))
        {
            return false;
        }

        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        dbContext.Notices.AddRange(caseSet.Notices.Select(notice => NoticeEntity.Create(notice, timeProvider.GetUtcNow())));
        SyntheticSet.AddPassages(dbContext, passagesFile);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Asks every case <see cref="RunsPerCase"/> times, case by case. A model that cannot be reached ends it.</summary>
    private async Task RunCasesAsync(AgentCaseSet caseSet, List<CaseRun> runs, TextWriter output, CancellationToken cancellationToken)
    {
        // The agent the API runs, with one difference: its clock says the reference date of the cases.
        var agent = new TenderAgent(tools, chatClient, agentOptions, new ReferenceClock(caseSet.ReferenceDate, timeProvider), agentLogger);
        foreach (var @case in caseSet.Cases)
        {
            for (var run = 1; run <= RunsPerCase; run++)
            {
                CaseRun caseRun;
                try
                {
                    var response = await agent.AskAsync(@case.Question, cancellationToken).ConfigureAwait(false);
                    var trajectory = new Trajectory(
                        [.. response.ToolCalls.Select(call => new TrajectoryCall(call.Name, call.Arguments, call.Failed))],
                        response.LimitReached,
                        response.Refused);
                    caseRun = new CaseRun(@case.Id, run, trajectory, AgentScoring.Score(@case, trajectory), null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && !QuestionAnswerer.IsUnavailable(exception, cancellationToken))
                {
                    // No trajectory is made up for it: the run counts as not passed and nothing else.
                    caseRun = new CaseRun(@case.Id, run, null, null, exception.GetType().Name);
                }

                runs.Add(caseRun);
                await output.WriteLineAsync($"{@case.Id} run {run}: {caseRun.Error ?? (caseRun.Score!.Passed ? "passed" : "not passed")}").ConfigureAwait(false);
            }
        }
    }

    private AgentEvaluationResults Summarize(AgentCaseSet caseSet, List<CaseRun> runs, bool complete) => new(
        timeProvider.GetUtcNow(),
        typeof(AgentEvaluationRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1),
        agentOptions.Value.Model,
        embeddingOptions.Value.Model,
        caseSet.ReferenceDate,
        TenderAgent.MaxRoundTrips,
        TenderAgent.MaxToolCalls,
        (int)TenderAgent.GatheringLimit.TotalSeconds,
        caseSet.Cases.Count,
        RunsPerCase,
        complete,
        AgentScoring.Summarize(runs, TenderAgent.MaxToolCalls),
        [.. runs.GroupBy(run => run.Run).Select(group => AgentScoring.Summarize([.. group], TenderAgent.MaxToolCalls))],
        runs);

    /// <summary>Says the reference date as today. Time passes, and timers run, as on the clock it wraps.</summary>
    private sealed class ReferenceClock(DateOnly today, TimeProvider clock) : TimeProvider
    {
        public override long TimestampFrequency => clock.TimestampFrequency;

        // Noon, so the date is the same in Poland.
        public override DateTimeOffset GetUtcNow() => new(today, new TimeOnly(12, 0), TimeSpan.Zero);

        public override long GetTimestamp() => clock.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            clock.CreateTimer(callback, state, dueTime, period);
    }
}
