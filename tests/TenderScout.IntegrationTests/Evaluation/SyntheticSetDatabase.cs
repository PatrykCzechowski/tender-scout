using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenderScout.Evaluation;
using TenderScout.Infrastructure.Agent;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>Loads the synthetic CI set into a test database and builds the evaluation runner over it.</summary>
internal static class SyntheticSetDatabase
{
    public const string AnswerModel = "answer-model";

    public const string AgentModel = "agent-model";

    public static IReadOnlyList<GoldenRecord> Questions => GoldenRecord.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation", SyntheticSet.QuestionsFile));

    /// <returns>The connection string of a new database holding every synthetic passage with its stored vector.</returns>
    public static async Task<string> CreateAsync(PostgresFixture database, CancellationToken cancellationToken)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);
        SyntheticSet.AddPassages(dbContext, Path.Combine(AppContext.BaseDirectory, "Evaluation", SyntheticSet.PassagesFile));
        await dbContext.SaveChangesAsync(cancellationToken);
        return connection;
    }

    /// <summary>The services of the evaluation command, with fake models and endpoints where nothing listens.</summary>
    public static ServiceProvider CreateServices(
        string connection,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IChatClient chatClient,
        TimeProvider? timeProvider = null,
        string? agentModel = AgentModel)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embedding:Endpoint"] = "http://127.0.0.1:59998",
            ["Embedding:Model"] = "bge-m3",
            ["Chat:Endpoint"] = "http://127.0.0.1:59999",
            ["Chat:Model"] = AnswerModel,
            ["Agent:Model"] = agentModel,
        }).Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddDbContext<TenderScoutDbContext>(options => options.UseNpgsql(connection).UsePgvector());
        services.AddChunkEmbeddings();
        services.AddQuestionAnswering();
        return services
            .AddSingleton(generator)
            .AddSingleton(chatClient)
            .AddSingleton(timeProvider ?? TimeProvider.System)
            .AddScoped<EvaluationRunner>()
            .AddScoped<NoticeQueries>()
            .AddTenderAgent()
            .AddScoped<AgentEvaluationRunner>()
            .BuildServiceProvider();
    }
}
