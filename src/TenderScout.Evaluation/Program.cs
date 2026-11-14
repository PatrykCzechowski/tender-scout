using Microsoft.Extensions.AI;
using TenderScout.Evaluation;
using TenderScout.Infrastructure.Agent;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

const string RetrievalOnly = "--retrieval-only";
const string WriteFixture = "--write-fixture";
const string AgentCases = "--agent-cases";

if (args.Except([RetrievalOnly, WriteFixture, AgentCases]).Any())
{
    Console.Error.WriteLine($"Usage: dotnet run --project src/TenderScout.Evaluation [-- {RetrievalOnly} | {WriteFixture} | {AgentCases}]");
    return 1;
}

// The options are not passed to the host, which would read them as configuration.
var builder = Host.CreateApplicationBuilder();

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddChunkEmbeddings();
builder.Services.AddQuestionAnswering();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<EvaluationRunner>();
builder.Services.AddScoped<NoticeQueries>();
builder.Services.AddTenderAgent();
builder.Services.AddScoped<AgentEvaluationRunner>();

using var host = builder.Build();
var repositoryRoot = FindRepositoryRoot();

if (args.Contains(WriteFixture))
{
    await SyntheticSet.WriteVectorsAsync(host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), repositoryRoot, CancellationToken.None);
    return 0;
}

var resultsFolder = Path.Combine(repositoryRoot, "artifacts", "eval");
if (args.Contains(AgentCases))
{
    await using var agentScope = host.Services.CreateAsyncScope();
    return await agentScope.ServiceProvider.GetRequiredService<AgentEvaluationRunner>().RunAsync(
        Path.Combine(repositoryRoot, AgentCaseSet.Folder),
        Path.Combine(repositoryRoot, SyntheticSet.Folder, SyntheticSet.PassagesFile),
        resultsFolder,
        Console.Out);
}

var goldenSetPath = builder.Configuration["Evaluation:GoldenSetPath"];
if (string.IsNullOrWhiteSpace(goldenSetPath))
{
    Console.Error.WriteLine("Set Evaluation:GoldenSetPath in user secrets to the golden set JSONL file.");
    return 1;
}

await using var scope = host.Services.CreateAsyncScope();
return await scope.ServiceProvider.GetRequiredService<EvaluationRunner>().RunAsync(
    goldenSetPath,
    builder.Configuration["Evaluation:JudgeModel"]!,
    args.Contains(RetrievalOnly),
    resultsFolder,
    Console.Out);

// dotnet run starts in the project folder; the results and the synthetic set are relative to the repository root.
static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenderScout.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? Environment.CurrentDirectory;
}

/// <summary>Internal, so tests that also reference the API see only one <c>Program</c>.</summary>
internal sealed partial class Program;
