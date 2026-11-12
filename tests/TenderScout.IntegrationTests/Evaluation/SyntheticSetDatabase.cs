using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenderScout.Domain.Documents;
using TenderScout.Evaluation;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>Loads the synthetic CI set into a test database and builds the evaluation runner over it.</summary>
internal static class SyntheticSetDatabase
{
    public const string AnswerModel = "answer-model";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static IReadOnlyList<GoldenRecord> Questions => GoldenRecord.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation", SyntheticSet.QuestionsFile));

    /// <returns>The connection string of a new database holding every synthetic passage with its stored vector.</returns>
    public static async Task<string> CreateAsync(PostgresFixture database, CancellationToken cancellationToken)
    {
        var passages = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Evaluation", SyntheticSet.PassagesFile))
            .Select(line => JsonSerializer.Deserialize<Passage>(line, JsonOptions)!)
            .ToList();

        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);
        foreach (var group in passages.GroupBy(p => (p.DocId, p.PublicationNumber)))
        {
            var document = DocumentEntity.Create(
                new TenderDocument(group.Key.DocId, group.Key.PublicationNumber, "swz", "a.pdf", new string('a', 64), null),
                DateTimeOffset.UnixEpoch);
            dbContext.Documents.Add(document);
            await dbContext.SaveChangesAsync(cancellationToken);

            dbContext.DocumentChunks.AddRange(group.Select((p, position) => new DocumentChunkEntity
            {
                DocumentId = document.Id,
                Position = position,
                PageNumber = p.Page,
                Text = p.Text,
                Embedding = new Pgvector.Vector(p.Vector),
                EmbeddingModel = "bge-m3",
            }));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return connection;
    }

    /// <summary>The services of the evaluation command, with fake models and endpoints where nothing listens.</summary>
    public static ServiceProvider CreateServices(
        string connection,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IChatClient chatClient)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embedding:Endpoint"] = "http://127.0.0.1:59998",
            ["Embedding:Model"] = "bge-m3",
            ["Chat:Endpoint"] = "http://127.0.0.1:59999",
            ["Chat:Model"] = AnswerModel,
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
            .AddSingleton(TimeProvider.System)
            .AddScoped<EvaluationRunner>()
            .BuildServiceProvider();
    }

    private sealed record Passage(string DocId, string PublicationNumber, int Page, string Text, float[] Vector);
}
