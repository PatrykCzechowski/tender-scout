using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(TenderScout.IntegrationTests.RabbitMqFixture))]

namespace TenderScout.IntegrationTests;

/// <summary>One RabbitMQ broker for the whole test run.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();

    public Uri Uri => new(_container.GetConnectionString());

    /// <summary>Creates a virtual host only the calling test sees, so its queues and leftovers stay its own.</summary>
    /// <returns>The broker address with that virtual host.</returns>
    public async Task<Uri> CreateIsolatedVirtualHostAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        var user = Uri.UserInfo.Split(':')[0];

        await ExecAsync("add_vhost", name);
        await ExecAsync("set_permissions", "-p", name, user, ".*", ".*", ".*");

        return new UriBuilder(Uri) { Path = name }.Uri;
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private async Task ExecAsync(params string[] arguments)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", .. arguments]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {arguments[0]} failed: {result.Stderr}");
        }
    }
}
