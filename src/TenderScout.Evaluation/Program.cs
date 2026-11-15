using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
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

// The file at Evaluation:GoldenSetPath may hold real tender text, which must not leave the machine: a run that answers
// and judges it needs the local chat provider. It stops here, before it reads the file or calls a model, and no
// setting turns this off. The other runs call no chat model or use the invented data of this repository.
var hostedChat = !string.Equals(builder.Configuration["Chat:Provider"] ?? ChatModelOptions.Ollama, ChatModelOptions.Ollama, StringComparison.OrdinalIgnoreCase);
if (hostedChat && !args.Contains(RetrievalOnly) && !args.Contains(WriteFixture) && !args.Contains(AgentCases))
{
    Console.Error.WriteLine($"A golden set is answered and judged by local models only: set Chat:Provider to {ChatModelOptions.Ollama}, or run {RetrievalOnly}.");
    return 1;
}

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

// The host is never started, so its check of the settings has to be asked for: a missing key or model stops the
// command here, before it loads data or calls a model.
try
{
    host.Services.GetRequiredService<IStartupValidator>().Validate();
}
catch (Exception exception) when (exception is OptionsValidationException or AggregateException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
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
