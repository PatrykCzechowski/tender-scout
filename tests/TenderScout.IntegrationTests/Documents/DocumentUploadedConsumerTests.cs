using System.Diagnostics;
using System.Text;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenderScout.Contracts.Documents;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Documents;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Ingestion;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Documents;

/// <summary>
/// End to end over a real RabbitMQ broker and PostgreSQL. Each test gets its own virtual host, so a message
/// one test leaves behind cannot reach the consumers of the next one.
/// </summary>
public sealed class DocumentUploadedConsumerTests(PostgresFixture database, RabbitMqFixture rabbitMq) : IDisposable
{
    private const string Model = "bge-m3";
    private const int RetryLimit = 5;

    /// <summary>
    /// How long a test waits for a step of the pipeline before it fails. The slowest passing test of this class on the
    /// CI runner took 7.1 s, set-up included.
    /// </summary>
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(60);

    /// <summary>How long the two retry tests wait; the real backoff of five retries takes about 36 seconds.</summary>
    private static readonly TimeSpan RetriesLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Reads a list of the test harness as it is, without waiting. Every waiting read of these lists ends once the bus
    /// has been idle for the harness's inactivity limit (1.2 s by default), and it stays ended for the rest of the
    /// test: a test that spent that long before its first publish found the list empty and failed (#52). So the tests
    /// read the lists with a token that is already cancelled and wait for the outcome themselves.
    /// </summary>
    private static readonly CancellationToken NoWait = new(canceled: true);

    private readonly DocumentTestFolder _folder = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameMessagePublishedTwice_StoresOneDocument()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.WithText("Serwery"));

        await harness.Bus.Publish(message, Ct);
        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 2);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.NotEmpty(document.Chunks);
        Assert.Equal(document.CreatedAt, document.UpdatedAt);

        Assert.Equal(2, harness.Published.Select<ChunksCreated>(m => m.Context.Message.DocId == message.DocId, NoWait).Count());
    }

    [Fact]
    public async Task ImportRunning_PublishesRowsThatTheConsumerStores()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var docId = $"doc-{Guid.NewGuid():N}";
        _folder.Add(docId, "import.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await new DocumentImporter(harness.Bus, _folder.Options(), NullLogger<DocumentImporter>.Instance).RunAsync(Ct);
        await WaitForConsumedAsync(harness, docId, expected: 1);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Published);
        Assert.NotEmpty((await SingleDocumentAsync(docId)).Chunks);
    }

    [Fact]
    public async Task MultiPagePdf_StoresChunksInReadingOrderWithTheirPages()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var content = SyntheticPdf.Create(
            ["Rozdzial I", "Tekst ze strony pierwszej dla importu dokumentu."],
            ["Rozdzial II", "Tekst ze strony drugiej dla importu dokumentu."]);
        var message = CreateMessage(content);

        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 1);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.Equal(message.PublicationNumber, document.PublicationNumber);
        Assert.Equal(message.Kind, document.Kind);
        Assert.Equal(message.Path, document.Path);
        Assert.Equal(DocumentTestFolder.Hash(content), document.Sha256);
        var chunks = document.Chunks.OrderBy(c => c.Position).ToList();
        Assert.Equal([0, 1], chunks.Select(c => c.Position));
        Assert.Equal([1, 2], chunks.Select(c => c.PageNumber));
        Assert.Contains("pierwszej", chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedFile_ReplacesChunksOfTheSameDocument()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.WithText("Pierwsza wersja"));
        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 1);

        var changed = SyntheticPdf.WithText("Druga wersja");
        _folder.WriteFile(message.Path, changed);
        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 2);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.Equal(DocumentTestFolder.Hash(changed), document.Sha256);
        Assert.All(document.Chunks, c => Assert.Contains("Druga wersja", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DocumentWithoutText_IsSkippedOnceAndSendsNothingToTheGenerator()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.Create([], []));

        await harness.Bus.Publish(message, Ct);
        await harness.Bus.Publish(message, Ct);
        await WaitForEmbeddingAsync(harness, message.DocId, expected: 2);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.Equal(DocumentSkipReason.NoTextLayer, document.SkipReason);
        Assert.Empty(document.Chunks);
        Assert.Empty(_generator.Calls);
    }

    [Fact]
    public async Task WordFile_IsSkippedWithItsOwnReason()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage([0x50, 0x4B, 0x03, 0x04, 0x14, 0x00], ".docx");

        await harness.Bus.Publish(message, Ct);
        await WaitForEmbeddingAsync(harness, message.DocId, expected: 1);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.Equal(DocumentSkipReason.UnsupportedFormat, document.SkipReason);
        Assert.Empty(document.Chunks);
        Assert.Empty(_generator.Calls);
    }

    [Theory]
    [InlineData("this is not a pdf at all")]
    [InlineData("%PDF-1.7 and then the file breaks off")]
    public async Task CorruptedPdf_FaultsWithoutStoringAnything(string content)
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(Encoding.ASCII.GetBytes(content));

        await harness.Bus.Publish(message, Ct);
        await WaitUntilAsync(
            $"a fault of {message.DocId} published",
            () => Task.FromResult(harness.Published.Select<Fault<DocumentUploaded>>(f => f.Context.Message.Message.DocId == message.DocId, NoWait).Any()),
            StepLimit);

        await using var dbContext = database.CreateDbContext();
        Assert.False(await dbContext.Documents.AnyAsync(d => d.DocId == message.DocId, Ct));
        Assert.False(await harness.Published.Any<ChunksCreated>(m => m.Context.Message.DocId == message.DocId, Ct));
    }

    [Fact]
    public async Task FileRemovedBeforeProcessing_Faults()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.WithText("Serwery"));
        File.Delete(message.FullPath);

        await harness.Bus.Publish(message, Ct);

        await WaitUntilAsync(
            $"a fault of {message.DocId} published",
            () => Task.FromResult(harness.Published.Select<Fault<DocumentUploaded>>(f => f.Context.Message.Message.DocId == message.DocId, NoWait).Any()),
            StepLimit);
    }

    [Fact]
    public async Task Document_GoesThroughBothStepsAndEndsUpEmbedded()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.WithText("Serwery"));

        await harness.Bus.Publish(message, Ct);
        await WaitForEmbeddingAsync(harness, message.DocId, expected: 1);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.NotEmpty(document.Chunks);
        Assert.All(document.Chunks, chunk =>
        {
            Assert.Equal(Model, chunk.EmbeddingModel);
            Assert.Equal(FakeEmbeddingGenerator.VectorFor(chunk.Text), chunk.Embedding!.ToArray());
        });
    }

    [Fact]
    public async Task SameMessagePublishedTwice_EmbedsEachChunkOnce()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.WithText("Serwery"));

        await harness.Bus.Publish(message, Ct);
        await harness.Bus.Publish(message, Ct);
        await WaitForEmbeddingAsync(harness, message.DocId, expected: 2);

        var chunks = (await SingleDocumentAsync(message.DocId)).Chunks;
        Assert.Equal(chunks.Select(c => c.Text).Order(), _generator.Calls.SelectMany(call => call).Order());
    }

    [Fact]
    public async Task ChunksCreated_EmbedsOnlyThatDocument()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var first = await SaveDocumentAsync("Tekst pierwszego");
        var second = await SaveDocumentAsync("Tekst drugiego");

        await harness.Bus.Publish(new ChunksCreated(first), Ct);
        await WaitForEmbeddingAsync(harness, first, expected: 1);

        Assert.Equal(["Tekst pierwszego"], _generator.Calls.SelectMany(call => call));
        Assert.All((await SingleDocumentAsync(second)).Chunks, chunk => Assert.Null(chunk.Embedding));
    }

    [Fact]
    public async Task GeneratorRecoveringWithinTheRetries_EmbedsWithoutAFault()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var docId = await SaveDocumentAsync("Tekst dokumentu");
        var failures = 2;
        _generator.Transform = vectors => failures-- > 0 ? throw new HttpRequestException("Connection refused (simulated)") : vectors;

        await harness.Bus.Publish(new ChunksCreated(docId), Ct);
        await WaitUntilAsync($"every chunk of {docId} embedded", async () => (await SingleDocumentAsync(docId)).Chunks.All(c => c.Embedding is not null), RetriesLimit);

        Assert.Equal(3, _generator.Calls.Count);
        Assert.False(await harness.Published.Any<Fault<ChunksCreated>>(f => f.Context.Message.Message.DocId == docId, Ct));
    }

    /// <summary>Waits for the real backoff of five retries, about 36 seconds.</summary>
    [Fact]
    public async Task GeneratorThatKeepsFailing_IsRetriedThenFaults()
    {
        await using var provider = await CreateProviderAsync();
        var harness = await StartHarnessAsync(provider);
        var docId = await SaveDocumentAsync("Tekst dokumentu");
        _generator.Transform = _ => throw new HttpRequestException("Connection refused (simulated)");

        await harness.Bus.Publish(new ChunksCreated(docId), Ct);
        await WaitUntilAsync($"{1 + RetryLimit} calls to the generator", () => Task.FromResult(_generator.Calls.Count >= 1 + RetryLimit), RetriesLimit);

        Assert.True(await harness.Published.Any<Fault<ChunksCreated>>(f => f.Context.Message.Message.DocId == docId, Ct));
        Assert.Equal(1 + RetryLimit, _generator.Calls.Count);
        Assert.All((await SingleDocumentAsync(docId)).Chunks, chunk => Assert.Null(chunk.Embedding));
    }

    public void Dispose() => _folder.Dispose();

    private static async Task<ITestHarness> StartHarnessAsync(IServiceProvider provider)
    {
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return harness;
    }

    /// <summary>Waits until the upload consumer has handled the document <paramref name="expected"/> times, each without an exception.</summary>
    private static async Task WaitForConsumedAsync(ITestHarness harness, string docId, int expected)
    {
        var consumed = harness.GetConsumerHarness<DocumentUploadedConsumer>().Consumed;
        IReceivedMessage<DocumentUploaded>[] OfDocument() => [.. consumed.Select<DocumentUploaded>(m => m.Context.Message.DocId == docId, NoWait)];

        await WaitUntilAsync($"{expected} DocumentUploaded of {docId} consumed", () => Task.FromResult(OfDocument().Length >= expected), StepLimit);
        Assert.All(OfDocument(), m => Assert.Null(m.Exception));
    }

    /// <summary>Polls the condition every 100 ms; fails with the outcome that did not come once the limit has passed.</summary>
    private static async Task WaitUntilAsync(string outcome, Func<Task<bool>> condition, TimeSpan limit)
    {
        var waited = Stopwatch.StartNew();
        while (!await condition())
        {
            if (waited.Elapsed > limit)
            {
                Assert.Fail($"Waited {waited.Elapsed.TotalSeconds:0.0} s for {outcome}; it did not happen.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    /// <summary>Waits until the embedding consumer has handled the document <paramref name="expected"/> times, each without an exception.</summary>
    private static async Task WaitForEmbeddingAsync(ITestHarness harness, string docId, int expected)
    {
        var consumed = harness.GetConsumerHarness<ChunksCreatedConsumer>().Consumed;
        IReceivedMessage<ChunksCreated>[] OfDocument() => [.. consumed.Select<ChunksCreated>(m => m.Context.Message.DocId == docId, NoWait)];

        await WaitUntilAsync($"{expected} ChunksCreated of {docId} consumed", () => Task.FromResult(OfDocument().Length >= expected), StepLimit);
        Assert.All(OfDocument(), m => Assert.Null(m.Exception));
    }

    /// <summary>The production bus setup (consumers, retry, RabbitMQ topology) wrapped in the MassTransit test harness.</summary>
    private async Task<ServiceProvider> CreateProviderAsync()
    {
        var virtualHost = await rabbitMq.CreateIsolatedVirtualHostAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Endpoint"] = "http://ollama.test:11434",
                ["Embedding:Model"] = Model,
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddDbContext<TenderScoutDbContext>(options => options.UseNpgsql(database.ConnectionString).UsePgvector())
            .AddDocumentProcessing();

        services.AddChunkEmbeddings();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_generator);
        services.AddMassTransitTestHarness(bus => bus
            .AddDocumentConsumers()
            .UsingTenderScoutRabbitMq(virtualHost));

        // The retries of a failing message take longer than the default limit of a test.
        services.Configure<TestHarnessOptions>(options => options.TestTimeout = TimeSpan.FromMinutes(3));

        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<string> SaveDocumentAsync(string text)
    {
        var docId = $"doc-{Guid.NewGuid():N}";
        var document = new TenderDocument(docId, "100001-2026", "swz", "a.pdf", new string('a', 64), null);
        await using var dbContext = database.CreateDbContext();
        await new DocumentRepository(dbContext, TimeProvider.System).SaveAsync(document, [new DocumentChunk(0, 1, text)], Ct);
        return docId;
    }

    /// <summary>Tests share one database, so each uses its own document id.</summary>
    private DocumentUploaded CreateMessage(byte[] content, string extension = ".pdf")
    {
        var docId = $"doc-{Guid.NewGuid():N}";
        var path = docId + extension;
        _folder.WriteFile(path, content);
        return new DocumentUploaded(docId, "100001-2026", "swz", path, Path.Combine(_folder.DocumentsFolder, path));
    }

    private async Task<DocumentEntity> SingleDocumentAsync(string docId)
    {
        await using var dbContext = database.CreateDbContext();
        var rows = await dbContext.Documents.AsNoTracking().Include(d => d.Chunks).Where(d => d.DocId == docId).ToListAsync(Ct);
        return Assert.Single(rows);
    }
}
