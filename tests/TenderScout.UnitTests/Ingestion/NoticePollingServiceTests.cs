using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Ingestion;
using TenderScout.Infrastructure.Ted;
using TenderScout.Worker;

namespace TenderScout.UnitTests.Ingestion;

/// <summary>The worker's TED polling with a clock the test moves and a TED client that counts its calls.</summary>
public sealed class NoticePollingServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SwitchedOff_NeverCallsTed_NotAtStartNorAfterAnInterval()
    {
        var (service, ted, clock) = Create(new() { ["Ingestion:Enabled"] = "false" });

        await service.StartAsync(Ct);

        // Switched off, the service ends at once instead of waiting for the next tick.
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(0, ted.Calls);
    }

    [Fact]
    public async Task OnByDefault_PollsAtStartAndAfterEachInterval()
    {
        var (service, ted, clock) = Create([]);

        await service.StartAsync(Ct);
        await WaitForAsync(() => ted.Calls == 1);
        clock.Advance(TimeSpan.FromHours(1));
        await WaitForAsync(() => ted.Calls == 2);
        await service.StopAsync(Ct);

        Assert.Equal(2, ted.Calls);
    }

    private static (NoticePollingService Service, CountingTedClient Ted, FakeTimeProvider Clock) Create(Dictionary<string, string?> settings)
    {
        var ted = new CountingTedClient();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero));
        var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<ITedClient>(ted)
            .AddNoticeIngestion()
            .AddMassTransitTestHarness()
            .BuildServiceProvider();
        var service = new NoticePollingService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<NoticeIngestionOptions>>(),
            clock,
            NullLogger<NoticePollingService>.Instance);
        return (service, ted, clock);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var tries = 0; !condition(); tries++)
        {
            Assert.True(tries < 500, "The expected TED call did not come within 5 s.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class CountingTedClient : ITedClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<Notice>> SearchNoticesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<Notice>>([]);
        }
    }
}
