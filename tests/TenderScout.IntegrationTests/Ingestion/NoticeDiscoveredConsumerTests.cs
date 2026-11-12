using System.Text;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using TenderScout.Contracts.Notices;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Ingestion;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.IntegrationTests.Ingestion;

/// <summary>
/// End to end over a real RabbitMQ broker and PostgreSQL. The tests share the <c>notice-discovered</c>
/// queues, and xUnit runs the tests of one class one at a time, so they stay in this class.
/// </summary>
public sealed class NoticeDiscoveredConsumerTests(PostgresFixture database, RabbitMqFixture rabbitMq)
{
    private const string ErrorQueue = "notice-discovered_error";
    private const int RetryLimit = 2;

    /// <summary>
    /// The harness stops waiting for messages, for good, once the bus was idle this long, and it counts from before
    /// the bus has connected. Its default of 1.2 s is shorter than the first connection to the broker on a slow machine.
    /// </summary>
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameMessagePublishedTwice_StoresOneNotice()
    {
        await using var provider = CreateProvider();
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage();

        await harness.Bus.Publish(message, Ct);
        await harness.Bus.Publish(message, Ct);

        var consumed = new List<IReceivedMessage<NoticeDiscovered>>();
        await foreach (var received in harness.GetConsumerHarness<NoticeDiscoveredConsumer>().Consumed
            .SelectAsync<NoticeDiscovered>(m => m.Context.Message.PublicationNumber == message.PublicationNumber, Ct))
        {
            consumed.Add(received);
            if (consumed.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(2, consumed.Count);
        Assert.All(consumed, m => Assert.Null(m.Exception));

        await using var dbContext = database.CreateDbContext();
        var rows = await dbContext.Notices.AsNoTracking()
            .Where(n => n.PublicationNumber == message.PublicationNumber)
            .ToListAsync(Ct);
        var row = Assert.Single(rows);
        Assert.Equal(message.Title, row.Title);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedThenMovedToErrorQueue()
    {
        var repository = new FailingRepository(new TimeoutException("Database did not answer."));
        await using var provider = CreateProvider(services => services.AddSingleton<INoticeRepository>(repository));
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage();

        await harness.Bus.Publish(message, Ct);

        Assert.True(await harness.Published.Any<Fault<NoticeDiscovered>>(
            f => f.Context.Message.Message.PublicationNumber == message.PublicationNumber, Ct));
        Assert.Equal(1 + RetryLimit, repository.Attempts);
        Assert.True(await ErrorQueueContainsAsync(message.PublicationNumber), $"Message not found in {ErrorQueue}.");
    }

    [Fact]
    public async Task NonTransientFailure_GoesToErrorQueueWithoutRetry()
    {
        var repository = new FailingRepository(new InvalidOperationException("Bug in the consumer."));
        await using var provider = CreateProvider(services => services.AddSingleton<INoticeRepository>(repository));
        var harness = await StartHarnessAsync(provider);
        var message = CreateMessage();

        await harness.Bus.Publish(message, Ct);

        Assert.True(await harness.Published.Any<Fault<NoticeDiscovered>>(
            f => f.Context.Message.Message.PublicationNumber == message.PublicationNumber, Ct));
        Assert.Equal(1, repository.Attempts);
        Assert.True(await ErrorQueueContainsAsync(message.PublicationNumber), $"Message not found in {ErrorQueue}.");
    }

    /// <summary>The production bus setup (consumers, retry, RabbitMQ topology) wrapped in the MassTransit test harness.</summary>
    private ServiceProvider CreateProvider(Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:RetryLimit"] = $"{RetryLimit}",
                ["Ingestion:RetryMinInterval"] = "00:00:00.050",
                ["Ingestion:RetryMaxInterval"] = "00:00:00.200",
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddDbContext<TenderScoutDbContext>(options => options.UseNpgsql(database.ConnectionString).UsePgvector())
            .AddNoticePersistence()
            .AddNoticeIngestion()
            .AddMassTransitTestHarness(bus => bus
                .SetTestTimeouts(testInactivityTimeout: InactivityTimeout)
                .AddNoticeIngestionConsumers()
                .UsingTenderScoutRabbitMq(rabbitMq.Uri));

        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<ITestHarness> StartHarnessAsync(IServiceProvider provider)
    {
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return harness;
    }

    /// <summary>
    /// Drains the error queue looking for the message. MassTransit declares the queue and moves the
    /// message there right around the time it publishes the fault, so this polls briefly and treats a
    /// queue that does not exist yet as empty.
    /// </summary>
    private async Task<bool> ErrorQueueContainsAsync(string publicationNumber)
    {
        var factory = new ConnectionFactory { Uri = rabbitMq.Uri };
        await using var connection = await factory.CreateConnectionAsync(Ct);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            // A 404 on a missing queue closes the channel, so each attempt gets a fresh one.
            await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
            try
            {
                while (await channel.BasicGetAsync(ErrorQueue, autoAck: true, Ct) is { } delivery)
                {
                    if (Encoding.UTF8.GetString(delivery.Body.Span).Contains(publicationNumber, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == Constants.NotFound)
            {
                // Not declared yet; try again.
            }

            await Task.Delay(100, Ct);
        }

        return false;
    }

    private static NoticeDiscovered CreateMessage() =>
        new(
            PublicationNumber: $"it-{Guid.NewGuid():N}"[..32],
            Title: "Polska – Usługi informatyczne – Utrzymanie systemu SIOEZ",
            BuyerName: "Centralna Komisja Egzaminacyjna",
            MainCpvCode: "72000000",
            CpvCodes: ["72000000", "72100000"],
            PublicationDate: new DateOnly(2026, 11, 6),
            SubmissionDeadline: new DateOnly(2026, 12, 1),
            Link: new Uri("https://ted.europa.eu/pl/notice/-/detail/722762-2026"));

    private sealed class FailingRepository(Exception failure) : INoticeRepository
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<NoticeUpsertResult> UpsertAsync(Notice notice, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            throw failure;
        }
    }
}
