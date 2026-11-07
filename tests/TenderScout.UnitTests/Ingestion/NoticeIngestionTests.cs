using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenderScout.Contracts.Notices;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Ingestion;
using TenderScout.Infrastructure.Ted;

namespace TenderScout.UnitTests.Ingestion;

public sealed class NoticeIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_SearchesLookBackWindowAndPublishesOneMessagePerNotice()
    {
        var ted = new FakeTedClient(
            CreateNotice("722731-2026"),
            CreateNotice("722762-2026"));
        await using var provider = CreateProvider(ted, lookBackDays: 3);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        int published;
        await using (var scope = provider.CreateAsyncScope())
        {
            published = await scope.ServiceProvider.GetRequiredService<NoticeIngestion>()
                .RunOnceAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, published);
        Assert.Equal((new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 8)), ted.LastWindow);
        Assert.Equal(
            ["722731-2026", "722762-2026"],
            harness.Published.Select<NoticeDiscovered>(TestContext.Current.CancellationToken).Select(m => m.Context.Message.PublicationNumber).Order());
    }

    [Fact]
    public async Task RunOnceAsync_PublishedMessageCarriesAllNoticeFields()
    {
        var notice = CreateNotice("722731-2026");
        await using var provider = CreateProvider(new FakeTedClient(notice), lookBackDays: 0);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NoticeIngestion>().RunOnceAsync(TestContext.Current.CancellationToken);
        }

        var message = Assert.Single(harness.Published.Select<NoticeDiscovered>(TestContext.Current.CancellationToken)).Context.Message;
        Assert.Equal(notice, message.ToNotice() with { CpvCodes = notice.CpvCodes });
        Assert.Equal(notice.CpvCodes, message.CpvCodes);
    }

    private static ServiceProvider CreateProvider(FakeTedClient ted, int lookBackDays)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ingestion:LookBackDays"] = $"{lookBackDays}" })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Now))
            .AddSingleton<ITedClient>(ted)
            .AddNoticeIngestion()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(validateScopes: true);
    }

    private static Notice CreateNotice(string publicationNumber) =>
        new(
            publicationNumber,
            "Polska – Usługi informatyczne – Utrzymanie systemu",
            "Centralna Komisja Egzaminacyjna",
            "72000000",
            ["72000000", "72100000"],
            new DateOnly(2026, 11, 6),
            new DateOnly(2026, 12, 1),
            new Uri($"https://ted.europa.eu/pl/notice/-/detail/{publicationNumber}"));

    private sealed class FakeTedClient(params Notice[] notices) : ITedClient
    {
        public (DateOnly From, DateOnly To)? LastWindow { get; private set; }

        public Task<IReadOnlyList<Notice>> SearchNoticesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            LastWindow = (from, to);
            return Task.FromResult<IReadOnlyList<Notice>>(notices);
        }
    }
}
