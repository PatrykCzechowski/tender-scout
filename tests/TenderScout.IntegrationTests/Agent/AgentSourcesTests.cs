using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Agent;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Tools;
using TenderScout.IntegrationTests.Tools;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>The sources of an answer are what the tools returned, chosen by number, never what the model wrote.</summary>
public sealed class AgentSourcesTests(PostgresFixture database)
{
    private static readonly Notice Servers = new(
        "111111-2026", "Dostawa serwerów", "Gmina Przykładowo", "48820000", ["48820000", "72000000"], new DateOnly(2026, 11, 4), new DateOnly(2026, 12, 1), new Uri("https://example.org/111111"));

    private static readonly Notice Untitled = new("222222-2026", null, null, null, [], new DateOnly(2026, 11, 3), null, null);

    private static readonly Notice Portal = new("333333-2026", "Portal pacjenta", "Szpital Przykładowy", "72000000", [], new DateOnly(2026, 11, 2), null, null);

    private static readonly DocumentToolTests.Chunk[] Chunks =
    [
        new("111111-2026", "swz-a", 3, "Wadium wynosi 10 000 zł.", DocumentToolTests.Vector(1, 0)),
        new("111111-2026", "swz-b", 5, "Wadium wnosi się przelewem.", DocumentToolTests.Vector(1, 0.1f)),
        new("333333-2026", "swz-c", 8, "Wadium zwraca się po wyborze oferty.", DocumentToolTests.Vector(1, 0.2f)),
    ];

    private static readonly (string Tool, object Arguments) SearchDocuments = ("search_tender_documents", new { query = "wadium" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Citations_AreTheStoredPassagesInListOrder_NotTheModelsOrderOrWording()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [], Chunks));
        agent.Chat.Calls(SearchDocuments).Text().Answer("Według dokumentu: 'wadium 50 000 zł'.", 3, 1);

        var response = await agent.AskAsync();

        var returned = JsonSerializer.Deserialize<PassageSearchResult>(agent.Chat.ToolResult("call-1"), TenderTools.SerializerOptions)!.Passages;
        Assert.Equal(3, returned.Count);
        Assert.Equal("Według dokumentu: 'wadium 50 000 zł'.", response.Answer);
        Assert.Equal(
            new[] { returned[0], returned[2] }.Select(p => new Citation(p.DocId, p.PublicationNumber, p.Page, p.Text)),
            response.Citations);
        Assert.Empty(response.Notices);
    }

    [Fact]
    public async Task Notices_HaveTheSevenFieldsOfAListItem_WithNullsWritten()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers, Untitled, Portal]));
        agent.Chat.Calls(("get_notice", new { publicationNumber = "333333-2026" }), ("search_notices", new { })).Text().Answer("Dwa ogłoszenia.", 3, 1);

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        // The notice read with get_notice came first, then the search result, newest first.
        var notices = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("notices");
        Assert.Equal(["333333-2026", "222222-2026"], notices.EnumerateArray().Select(n => n.GetProperty("publicationNumber").GetString()));
        Assert.Equal(
            """{"publicationNumber":"222222-2026","title":null,"buyerName":null,"mainCpvCode":null,"publicationDate":"2026-11-03","submissionDeadline":null,"link":null}""",
            notices[1].GetRawText());
        Assert.Equal(
            ["publicationNumber", "title", "buyerName", "mainCpvCode", "publicationDate", "submissionDeadline", "link"],
            notices[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task InvalidAndRepeatedNumbers_AreDropped_AndNoticesAreNumberedBeforePassages()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers, Portal], Chunks[0]));
        agent.Chat.Calls(SearchDocuments, ("search_notices", new { })).Text().Answer("Odpowiedź.", 3, 0, -1, 99, 3, 1, 1);

        var response = await agent.AskAsync();

        Assert.Equal("111111-2026", Assert.Single(response.Notices).PublicationNumber);
        Assert.Equal(new Citation("swz-a", "111111-2026", 3, "Wadium wynosi 10 000 zł."), Assert.Single(response.Citations));
        var sources = agent.Chat.Requests[2].Messages[1].Text;
        Assert.Contains("[1] ogłoszenie 111111-2026: tytuł: Dostawa serwerów; zamawiający: Gmina Przykładowo; główny kod CPV: 48820000; opublikowano: 2026-11-04; termin składania ofert: 2026-12-01", sources, StringComparison.Ordinal);
        Assert.Contains("[2] ogłoszenie 333333-2026: tytuł: Portal pacjenta; zamawiający: Szpital Przykładowy; główny kod CPV: 72000000; opublikowano: 2026-11-02; termin składania ofert: brak", sources, StringComparison.Ordinal);
        Assert.Contains("[3] dokument swz-a, ogłoszenie 111111-2026, strona 3\nWadium wynosi 10 000 zł.", sources.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"answer": "Brak danych w podanych źródłach.", "citations": []}""")]
    [InlineData("""{"answer": "Wymyślona odpowiedź.", "citations": [0, 99]}""")]
    [InlineData("""{"answer": "", "citations": [1]}""")]
    [InlineData("""{"answer": "Bez pola z cytowaniami."}""")]
    [InlineData("To nie jest poprawny JSON {answer: ...")]
    public async Task AnswerWithoutAValidSourceNumber_IsRefused(string reply)
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        agent.Chat.Calls(("search_notices", new { })).Text().Text(reply);

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        // Every field is there, and the empty lists are lists.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["refused", "answer", "citations", "notices", "limitReached", "toolCalls"], body.EnumerateObject().Select(p => p.Name));
        Assert.True(body.GetProperty("refused").GetBoolean());
        Assert.Equal(QuestionAnswerer.RefusalMessage, body.GetProperty("answer").GetString());
        Assert.Equal("[]", body.GetProperty("citations").GetRawText());
        Assert.Equal("[]", body.GetProperty("notices").GetRawText());
        Assert.False(body.GetProperty("limitReached").GetBoolean());
        Assert.Equal(1, body.GetProperty("toolCalls").GetArrayLength());
    }

    [Theory]
    [InlineData("2026-12-31T23:30:00Z", "Dzisiaj jest 2027-01-01 (piątek).")]
    [InlineData("2027-06-30T22:30:00Z", "Dzisiaj jest 2027-07-01 (czwartek).")]
    public async Task TodaysDateInPoland_IsGivenToTheGatheringModel(string utcNow, string expected)
    {
        await using var agent = new AgentTestHost(database.ConnectionString);
        agent.Clock.SetUtcNow(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture));
        agent.Chat.Text();

        await agent.AskAsync();

        Assert.Contains(expected, agent.Chat.Requests[0].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothPhases_UseTheAgentModelAtTemperatureZero_AndOnlyGatheringHasTools()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        agent.Chat.Calls(("search_notices", new { })).Text().Answer("Jest jedno ogłoszenie.", 1);

        var response = await agent.PostAsync(new { question = AgentTestHost.Question });

        var gathering = agent.Chat.Requests[0].Options!;
        var answering = agent.Chat.Requests[2].Options!;
        Assert.Equal(
            (AgentTestHost.AgentModel, 0f, TenderAgent.MaxGatheringTokens, 3, TenderAgent.ContextTokens),
            (gathering.ModelId, gathering.Temperature, gathering.MaxOutputTokens, gathering.Tools!.Count, gathering.AdditionalProperties!["num_ctx"]));
        Assert.Equal(
            (AgentTestHost.AgentModel, 0f, 512, null, TenderAgent.ContextTokens),
            (answering.ModelId, answering.Temperature, answering.MaxOutputTokens, answering.Tools, answering.AdditionalProperties!["num_ctx"]));
        Assert.IsType<ChatResponseFormatJson>(answering.ResponseFormat);
        Assert.Equal([ChatRole.System, ChatRole.User], agent.Chat.Requests[2].Messages.Select(m => m.Role));
        Assert.StartsWith($"Pytanie: {AgentTestHost.Question}", agent.Chat.Requests[2].Messages[1].Text, StringComparison.Ordinal);

        // The first three fields read as a response of /ask.
        var ask = (await response.Content.ReadFromJsonAsync<AskResponse>(Ct))!;
        Assert.Equal((false, "Jest jedno ogłoszenie."), (ask.Refused, ask.Answer));
        Assert.Empty(ask.Citations);
    }

    [Fact]
    public async Task NoticeThatIsNotStored_IsNotASource()
    {
        await using var agent = new AgentTestHost(await AgentTestHost.SeedAsync(database, [Servers]));
        agent.Chat
            .Calls(("get_notice", new { publicationNumber = "999999-2026" })).Text()
            .Calls(("get_notice", new { publicationNumber = "999999-2026" }), ("get_notice", new { publicationNumber = "111111-2026" })).Text()
            .Answer("Jest jedno ogłoszenie.", 1, 2);

        var alone = await agent.AskAsync();
        var withAnother = await agent.AskAsync();

        Assert.True(alone.Refused);
        Assert.False(Assert.Single(alone.ToolCalls).Failed);
        Assert.Equal("111111-2026", Assert.Single(withAnother.Notices).PublicationNumber);
        Assert.DoesNotContain("999999-2026", agent.Chat.Requests[4].Messages[1].Text, StringComparison.Ordinal);
        Assert.Equal(5, agent.Chat.Requests.Count);
    }
}
