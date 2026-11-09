using System.Text;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenderScout.Contracts.Documents;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Documents;
using TenderScout.Infrastructure.Ingestion;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Documents;

/// <summary>
/// End to end over a real RabbitMQ broker and PostgreSQL. The tests share the <c>document-uploaded</c>
/// queue, and xUnit runs the tests of one class one at a time, so they stay in this class.
/// </summary>
public sealed class DocumentUploadedConsumerTests(PostgresFixture database, RabbitMqFixture rabbitMq) : IDisposable
{
    private readonly DocumentTestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameMessagePublishedTwice_StoresOneDocument()
    {
        await using var provider = CreateProvider();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.WithText("Serwery"));

        await harness.Bus.Publish(message, Ct);
        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 2);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.NotEmpty(document.Chunks);
        Assert.Equal(document.CreatedAt, document.UpdatedAt);

        var published = 0;
        await foreach (var _ in harness.Published.SelectAsync<ChunksCreated>(m => m.Context.Message.DocId == message.DocId, Ct))
        {
            if (++published == 2)
            {
                break;
            }
        }

        Assert.Equal(2, published);
    }

    [Fact]
    public async Task ImportRunning_PublishesRowsThatTheConsumerStores()
    {
        await using var provider = CreateProvider();
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
        await using var provider = CreateProvider();
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
        await using var provider = CreateProvider();
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
    public async Task DocumentWithoutText_IsSkippedOnceAndPublishesNothing()
    {
        await using var provider = CreateProvider();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.Create([], []));

        await harness.Bus.Publish(message, Ct);
        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 2);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.Equal(DocumentSkipReason.NoTextLayer, document.SkipReason);
        Assert.Empty(document.Chunks);
        Assert.False(await harness.Published.Any<ChunksCreated>(m => m.Context.Message.DocId == message.DocId, Ct));
    }

    [Fact]
    public async Task WordFile_IsSkippedWithItsOwnReason()
    {
        await using var provider = CreateProvider();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage([0x50, 0x4B, 0x03, 0x04, 0x14, 0x00], ".docx");

        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 1);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.Equal(DocumentSkipReason.UnsupportedFormat, document.SkipReason);
        Assert.Empty(document.Chunks);
        Assert.False(await harness.Published.Any<ChunksCreated>(m => m.Context.Message.DocId == message.DocId, Ct));
    }

    [Theory]
    [InlineData("this is not a pdf at all")]
    [InlineData("%PDF-1.7 and then the file breaks off")]
    public async Task CorruptedPdf_FaultsWithoutStoringAnything(string content)
    {
        await using var provider = CreateProvider();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(Encoding.ASCII.GetBytes(content));

        await harness.Bus.Publish(message, Ct);

        Assert.True(await harness.Published.Any<Fault<DocumentUploaded>>(f => f.Context.Message.Message.DocId == message.DocId, Ct));
        await using var dbContext = database.CreateDbContext();
        Assert.False(await dbContext.Documents.AnyAsync(d => d.DocId == message.DocId, Ct));
        Assert.False(await harness.Published.Any<ChunksCreated>(m => m.Context.Message.DocId == message.DocId, Ct));
    }

    [Fact]
    public async Task FileRemovedBeforeProcessing_Faults()
    {
        await using var provider = CreateProvider();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.WithText("Serwery"));
        File.Delete(message.FullPath);

        await harness.Bus.Publish(message, Ct);

        Assert.True(await harness.Published.Any<Fault<DocumentUploaded>>(f => f.Context.Message.Message.DocId == message.DocId, Ct));
    }

    public void Dispose() => _folder.Dispose();

    private static async Task<ITestHarness> StartHarnessAsync(IServiceProvider provider)
    {
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return harness;
    }

    private static async Task WaitForConsumedAsync(ITestHarness harness, string docId, int expected)
    {
        var consumed = new List<IReceivedMessage<DocumentUploaded>>();
        await foreach (var received in harness.GetConsumerHarness<DocumentUploadedConsumer>().Consumed
            .SelectAsync<DocumentUploaded>(m => m.Context.Message.DocId == docId, Ct))
        {
            consumed.Add(received);
            if (consumed.Count == expected)
            {
                break;
            }
        }

        Assert.Equal(expected, consumed.Count);
        Assert.All(consumed, m => Assert.Null(m.Exception));
    }

    /// <summary>The production bus setup (consumer, retry, RabbitMQ topology) wrapped in the MassTransit test harness.</summary>
    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContext<TenderScoutDbContext>(options => options.UseNpgsql(database.ConnectionString).UsePgvector())
            .AddDocumentProcessing()
            .AddMassTransitTestHarness(bus => bus
                .AddDocumentConsumers()
                .UsingTenderScoutRabbitMq(rabbitMq.Uri));

        return services.BuildServiceProvider(validateScopes: true);
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
