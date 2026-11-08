using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(TenderScout.IntegrationTests.RabbitMqFixture))]

namespace TenderScout.IntegrationTests;

/// <summary>One RabbitMQ broker for the whole test run.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();

    public Uri Uri => new(_container.GetConnectionString());

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
