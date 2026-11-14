using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Polly.Timeout;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Persistence;
using TenderScout.IntegrationTests.Security;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Answering;

/// <summary>
/// The API in memory against PostgreSQL, with fake embedding and chat clients. Chunk vectors are fixed, and the
/// question's vector is passed in, so cosine similarities are known: (1, 0) against (1, 0) is 1, against (0, 1) is 0.
/// </summary>
public sealed class AskEndpointTests(PostgresFixture database)
{
    private const string Question = "Jaka jest wysokość wadium?";

    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeChatClient _chat = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Answer_CitesTheStoredPassagesTheModelChose()
    {
        var connection = await SeedAsync(
            new("654321-2026", "swz-01", 3, "Wadium wynosi 15 000 zł.", Vector(1, 0)),
            new("654321-2026", "swz-01", 4, "Termin składania ofert upływa 1 grudnia.", Vector(0.9f, 0.1f)),
            new("111222-2026", "swz-02", 7, "Wadium wnosi się przed upływem terminu składania ofert.", Vector(0.8f, 0.2f)));
        _chat.Reply = """{"answer": "Według dokumentu: 'wadium 50 000 zł'.", "citations": [1, 2]}""";

        var answer = await ReadAnswerAsync(await AskAsync(connection, new { question = Question }));

        Assert.False(answer.Refused);
        Assert.Equal("Według dokumentu: 'wadium 50 000 zł'.", answer.Answer);
        Assert.Equal(
            [
                new Citation("swz-01", "654321-2026", 3, "Wadium wynosi 15 000 zł."),
                new Citation("swz-02", "111222-2026", 7, "Wadium wnosi się przed upływem terminu składania ofert."),
            ],
            answer.Citations);
    }

    [Fact]
    public async Task ChatRequest_HasTheInstructionsTheQuestionFiveNumberedPassagesAndTheOptions()
    {
        var connection = await SeedAsync([.. Enumerable.Range(1, 6).Select(i =>
            new Passage("654321-2026", "swz-01", i, $"Kryterium oceny ofert numer {i}", Vector(1, i / 10f)))]);

        await AskAsync(connection, new { question = "Jakie są kryteria oceny ofert?" });

        var (messages, options) = Assert.Single(_chat.Calls);
        Assert.Equal(QuestionAnswerer.SystemPrompt, messages[0].Text);
        var prompt = messages[1].Text;
        Assert.StartsWith("Pytanie: Jakie są kryteria oceny ofert?", prompt, StringComparison.Ordinal);
        Assert.Contains("[1] dokument swz-01, strona 1\nKryterium oceny ofert numer 1", prompt, StringComparison.Ordinal);
        Assert.Contains("[5] dokument swz-01, strona 5", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[6]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("numer 6", prompt, StringComparison.Ordinal);
        Assert.Equal(2, messages.Count);

        Assert.Equal(0, options!.Temperature);
        Assert.Equal(512, options.MaxOutputTokens);
        Assert.Equal(8192, options.AdditionalProperties!["num_ctx"]);
        var format = Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        Assert.Equal(JsonValueKind.Object, format.Schema!.Value.GetProperty("properties").GetProperty("citations").ValueKind);
    }

    [Fact]
    public async Task PublicationNumber_LimitsThePassagesToThatTender()
    {
        var connection = await SeedAsync(
            new("111111-2026", "swz-a", 1, "Termin wykonania: 30 dni.", Vector(1, 0)),
            new("222222-2026", "swz-b", 2, "Termin wykonania: 60 dni.", Vector(0.8f, 0.2f)));

        var answer = await ReadAnswerAsync(await AskAsync(
            connection,
            new { question = "Jaki jest termin wykonania?", publicationNumber = "222222-2026" }));

        Assert.DoesNotContain("swz-a", Assert.Single(_chat.Calls).Messages[1].Text, StringComparison.Ordinal);
        var citation = Assert.Single(answer.Citations);
        Assert.Equal(("swz-b", "222222-2026"), (citation.DocId, citation.PublicationNumber));
    }

    [Fact]
    public async Task UnknownTender_IsRefusedWithoutCallingTheModel()
    {
        var connection = await SeedAsync(new Passage("111111-2026", "swz-a", 1, "Wadium wynosi 15 000 zł.", Vector(1, 0)));

        var answer = await ReadAnswerAsync(await AskAsync(connection, new { question = Question, publicationNumber = "999999-2026" }));

        AssertRefusal(answer);
        Assert.Empty(_chat.Calls);
    }

    [Fact]
    public async Task InvalidAndRepeatedCitations_AreDropped_InTheModelsOrder()
    {
        var connection = await SeedAsync(
            new("654321-2026", "swz-01", 1, "Wadium pierwsze", Vector(1, 0)),
            new("654321-2026", "swz-01", 2, "Wadium drugie", Vector(0.9f, 0.1f)),
            new("654321-2026", "swz-01", 3, "Wadium trzecie", Vector(0.8f, 0.2f)));
        _chat.Reply = """{"answer": "Odpowiedź.", "citations": [2, 2, 7, 0, -1, 1, 2]}""";

        var answer = await ReadAnswerAsync(await AskAsync(connection, new { question = "wadium" }));

        Assert.Equal(["Wadium drugie", "Wadium pierwsze"], answer.Citations.Select(c => c.Passage));
    }

    [Theory]
    [InlineData("""{"answer": "Brak danych w podanych fragmentach.", "citations": []}""")]
    [InlineData("""{"answer": "Wymyślona odpowiedź.", "citations": [0, 99]}""")]
    [InlineData("""{"answer": "", "citations": [1]}""")]
    [InlineData("""{"answer": "Bez pola z cytowaniami."}""")]
    [InlineData("To nie jest poprawny JSON {answer: ...")]
    public async Task ReplyWithoutAValidCitation_IsRefused(string reply)
    {
        var connection = await SeedAsync(new Passage("654321-2026", "swz-01", 1, "Wadium wynosi 15 000 zł.", Vector(1, 0)));
        _chat.Reply = reply;

        AssertRefusal(await ReadAnswerAsync(await AskAsync(connection, new { question = Question })));
    }

    [Fact]
    public async Task DistantQuestion_IsRefusedWithoutCallingTheModel()
    {
        var connection = await SeedAsync(new Passage("654321-2026", "swz-01", 1, "Termin wykonania zamówienia.", Vector(1, 0)));

        var answer = await ReadAnswerAsync(await AskAsync(connection, new { question = Question }, queryVector: Vector(0, 1)));

        AssertRefusal(answer);
        Assert.Empty(_chat.Calls);
    }

    [Fact]
    public async Task NumberFromTheQuestion_ReachesTheModelDespiteALowSimilarity()
    {
        var connection = await SeedAsync(
            new Passage("654321-2026", "swz-01", 2, "Przedmiot zamówienia obejmuje kod CPV 72000000-5 dla usług IT.", Vector(1, 0)));

        var answer = await ReadAnswerAsync(await AskAsync(
            connection,
            new { question = "Czego dotyczy kod CPV 72000000?" },
            queryVector: Vector(0.1f, 1)));

        Assert.False(answer.Refused);
        Assert.Single(_chat.Calls);
    }

    [Fact]
    public async Task EmptyDatabase_IsRefusedWithoutCallingTheModel()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();

        AssertRefusal(await ReadAnswerAsync(await AskAsync(connection, new { question = Question })));
        Assert.Empty(_chat.Calls);
    }

    [Theory]
    [InlineData(null, null, "question")]
    [InlineData("", null, "question")]
    [InlineData("   ", null, "question")]
    [InlineData("long", null, "question")]
    [InlineData(Question, "invalid-pub-no", "publicationNumber")]
    [InlineData(Question, "1234567-2026", "publicationNumber")]
    [InlineData(Question, "   ", "publicationNumber")]
    public async Task InvalidInput_Is400_WithoutCallingAnyModel(string? question, string? publicationNumber, string field)
    {
        question = question == "long" ? new string('a', 1001) : question;

        var response = await AskAsync(database.ConnectionString, new { question, publicationNumber });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(_generator.Calls);
        Assert.Empty(_chat.Calls);
    }

    [Fact]
    public async Task EmbeddingModelDown_Is503_WithoutCallingTheChatModel()
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        _generator.FailOnCall = 1;

        var response = await AskAsync(connection, new { question = Question });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(_chat.Calls);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("model not pulled")]
    [InlineData("client timeout")]
    [InlineData("pipeline timeout")]
    public async Task ChatModelDown_Is503_WithoutDetails(string failure)
    {
        const string passage = "Wadium wynosi 15 000 zł.";
        var connection = await SeedAsync(new Passage("654321-2026", "swz-01", 1, passage, Vector(1, 0)));
        _chat.Failure = failure switch
        {
            "connection" => new HttpRequestException("Connection refused (http://192.0.2.99:11434/api/chat)"),
            "model not pulled" => new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound),
            "client timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 90 seconds elapsing."),
            _ => new TimeoutRejectedException("The operation didn't complete within the allowed timeout."),
        };

        var response = await AskAsync(connection, new { question = Question });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("192.0.2.99", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at TenderScout.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(passage, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_StartsAndIsHealthy_WithOllamaUnreachable()
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(web => Configure(web, database.ConnectionString));
        using var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive", Ct)).StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesAskWithItsRequestBodyAndResponses()
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(web => Configure(web, database.ConnectionString));
        using var client = api.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        var ask = document.GetProperty("paths").GetProperty("/ask").GetProperty("post");
        Assert.True(ask.TryGetProperty("requestBody", out _));
        Assert.Equal(["200", "400", "503", "401", "403"], ask.GetProperty("responses").EnumerateObject().Select(r => r.Name));
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task ApiReference_IsServedOnlyInDevelopment(string environment, HttpStatusCode expected)
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            Configure(web, database.ConnectionString);
            web.UseEnvironment(environment);
        });
        using var client = api.CreateClient();

        Assert.Equal(expected, (await client.GetAsync("/scalar/", Ct)).StatusCode);
    }

    private static void AssertRefusal(AskResponse answer)
    {
        Assert.True(answer.Refused);
        Assert.Equal(QuestionAnswerer.RefusalMessage, answer.Answer);
        Assert.Empty(answer.Citations);
    }

    private static float[] Vector(float first, float second)
    {
        var vector = new float[ChunkEmbedding.Dimensions];
        vector[0] = first;
        vector[1] = second;
        return vector;
    }

    /// <summary>Points the API at the test database and at ports where nothing listens.</summary>
    private static void Configure(IWebHostBuilder web, string connection)
    {
        web.UseSetting("ConnectionStrings:tenderscout", connection);
        web.UseSetting("Embedding:Endpoint", "http://127.0.0.1:59998");
        web.UseSetting("Chat:Endpoint", "http://127.0.0.1:59999");
        web.UseTestIssuer();
    }

    private static async Task<AskResponse> ReadAnswerAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AskResponse>(Ct))!;
    }

    private async Task<HttpResponseMessage> AskAsync(string connection, object body, float[]? queryVector = null)
    {
        var vector = queryVector ?? Vector(1, 0);
        _generator.Transform = _ => [vector];

        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            Configure(web, connection);
            web.ConfigureTestServices(services => services
                .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_generator)
                .AddSingleton<IChatClient>(_chat));
        });
        using var client = api.CreateClient().WithToken();

        var response = await client.PostAsJsonAsync("/ask", body, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    /// <summary>Stores chunks in the given order, one document per document id.</summary>
    private async Task<string> SeedAsync(params Passage[] passages)
    {
        var connection = await database.CreateIsolatedDatabaseAsync();
        await using var dbContext = database.CreateDbContext(connection);
        foreach (var group in passages.GroupBy(p => (p.PublicationNumber, p.DocId)))
        {
            var document = DocumentEntity.Create(
                new TenderDocument(group.Key.DocId, group.Key.PublicationNumber, "swz", "a.pdf", new string('a', 64), null),
                Now);
            dbContext.Documents.Add(document);
            await dbContext.SaveChangesAsync(Ct);

            dbContext.DocumentChunks.AddRange(group.Select((p, position) => new DocumentChunkEntity
            {
                DocumentId = document.Id,
                Position = position,
                PageNumber = p.Page,
                Text = p.Text,
                Embedding = new Pgvector.Vector(p.Vector),
                EmbeddingModel = "bge-m3",
            }));
            await dbContext.SaveChangesAsync(Ct);
        }

        return connection;
    }

    private sealed record Passage(string PublicationNumber, string DocId, int Page, string Text, float[] Vector);
}
