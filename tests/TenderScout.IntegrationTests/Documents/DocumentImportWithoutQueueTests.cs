using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenderScout.Infrastructure.Documents;
using TenderScout.Infrastructure.Ingestion;
using TenderScout.Tests.Shared;
using Testcontainers.RabbitMq;

namespace TenderScout.IntegrationTests.Documents;

/// <summary>Uses its own broker: on the shared one, other tests have already bound the queue.</summary>
public sealed class DocumentImportWithoutQueueTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _broker = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private readonly DocumentTestFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _broker.StartAsync();

    [Fact]
    public async Task Run_WithNoQueueBound_RejectsTheRowInsteadOfReportingSuccess()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.UsingTenderScoutRabbitMq(new Uri(_broker.GetConnectionString())))
            .BuildServiceProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        _folder.Add("swz-001", "a.pdf", SyntheticPdf.WithText("Serwery"));

        var report = await new DocumentImporter(harness.Bus, _folder.Options(), NullLogger<DocumentImporter>.Instance).RunAsync(Ct);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.Published);
        Assert.Contains("swz-001", Assert.Single(report.Rejected), StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        _folder.Dispose();
        await _broker.DisposeAsync();
    }
}
