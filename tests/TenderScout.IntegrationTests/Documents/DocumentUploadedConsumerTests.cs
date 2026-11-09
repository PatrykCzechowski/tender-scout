using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task DocumentWithoutText_IsRecordedAsSkippedAndPublishesNothing()
    {
        await using var provider = CreateProvider();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage(SyntheticPdf.Create([], []));

        await harness.Bus.Publish(message, Ct);
        await WaitForConsumedAsync(harness, message.DocId, expected: 1);

        var document = await SingleDocumentAsync(message.DocId);
        Assert.Equal(DocumentSkipReason.NoTextLayer, document.SkipReason);
        Assert.Empty(document.Chunks);
        Assert.False(await harness.Published.Any<ChunksCreated>(m => m.Context.Message.DocId == message.DocId, Ct));
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
    private DocumentUploaded CreateMessage(byte[] content)
    {
        var docId = $"doc-{Guid.NewGuid():N}";
        var path = $"{docId}.pdf";
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
