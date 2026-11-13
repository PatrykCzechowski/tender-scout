extern alias McpHost;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TenderScout.IntegrationTests.Tools;

/// <summary>
/// The MCP host in memory against a test database, with an MCP client connected to its HTTP endpoint, so every
/// call in the tests goes through the protocol.
/// </summary>
internal sealed class McpTestHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<McpHost::Program> _factory;
    private readonly ErrorLog _log;

    private McpTestHost(WebApplicationFactory<McpHost::Program> factory, ErrorLog log, McpClient client)
    {
        _factory = factory;
        _log = log;
        Client = client;
    }

    public McpClient Client { get; }

    /// <summary>What the server logged at error level.</summary>
    public IReadOnlyCollection<string> Errors => _log.Messages;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="generator">Replaces the embedding model; without it the host points at a port where nothing listens.</param>
    public static async Task<McpTestHost> StartAsync(string connection, IEmbeddingGenerator<string, Embedding<float>>? generator = null)
    {
        var log = new ErrorLog();
        var factory = new WebApplicationFactory<McpHost::Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:tenderscout", connection);
            web.UseSetting("Embedding:Endpoint", "http://127.0.0.1:59998");
            web.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(log);
                if (generator is not null)
                {
                    services.AddSingleton(generator);
                }
            });
        });

        return new McpTestHost(factory, log, await ConnectAsync(factory));
    }

    /// <summary>A second, independent client of the same server.</summary>
    public Task<McpClient> ConnectAsync() => ConnectAsync(_factory);

    public HttpClient CreateHttpClient() => _factory.CreateClient();

    public async Task<CallToolResult> CallAsync(string tool, object? arguments = null)
    {
        var values = JsonSerializer.SerializeToNode(arguments ?? new { })!.AsObject()
            .ToDictionary(p => p.Key, p => (object?)p.Value);
        return await Client.CallToolAsync(tool, values, cancellationToken: Ct);
    }

    /// <summary>Calls a tool that must succeed and reads its structured result.</summary>
    public async Task<T> CallAsync<T>(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.True(result.IsError is not true, Text(result));
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result.StructuredContent), JsonSerializerOptions.Web)!;
    }

    /// <summary>Calls a tool that must fail and returns the error text.</summary>
    public async Task<string> CallForErrorAsync(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.True(result.IsError);
        return Text(result);
    }

    public static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<McpHost::Program> factory)
    {
        var http = factory.CreateClient();
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true), cancellationToken: Ct);
    }

    private sealed class ErrorLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Enqueue($"{formatter(state, exception)} {exception}");
            }
        }

        public void Dispose()
        {
        }
    }
}
