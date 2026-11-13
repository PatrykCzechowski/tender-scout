using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TenderScout.Domain.Notices;
using TenderScout.Infrastructure.Agent;
using TenderScout.Infrastructure.Persistence;
using TenderScout.IntegrationTests.Tools;
using TenderScout.Tests.Shared;

namespace TenderScout.IntegrationTests.Agent;

/// <summary>
/// The API in memory against a test database, with a scripted chat model, the fake embedding generator, a clock the
/// test moves and a record of what was logged. Only the model's replies are scripted: the real tools run.
/// </summary>
internal sealed class AgentTestHost : IAsyncDisposable
{
    public const string Question = "Jakie wadium trzeba wnieść w przetargu na serwery?";
    public const string AgentModel = "agent-model";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly Log _log = new();

    /// <param name="agentModel">The <c>Agent:Model</c> setting; <see langword="null"/> leaves it out.</param>
    public AgentTestHost(string connection, string? agentModel = AgentModel)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:tenderscout", connection);
            web.UseSetting("Embedding:Endpoint", "http://127.0.0.1:59998");
            web.UseSetting("Chat:Endpoint", "http://127.0.0.1:59999");
            web.UseSetting("Agent:Model", agentModel);
            web.ConfigureTestServices(services => services
                .AddSingleton<IChatClient>(Chat)
                .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Generator)
                .AddSingleton<TimeProvider>(Clock)
                .AddSingleton<ILoggerProvider>(_log));
        });
    }

    public ScriptedChatClient Chat { get; } = new();

    public FakeEmbeddingGenerator Generator { get; } = new() { Transform = _ => [DocumentToolTests.Vector(1, 0)] };

    /// <summary>Friday 13 November 2026, 10:00 in Poland.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    /// <summary>What the agent logged, in order.</summary>
    public IEnumerable<string> AgentLog => _log.Entries.Where(e => e.Category == typeof(TenderAgent).FullName).Select(e => e.Message);

    /// <summary>Everything logged at information level or above, by anything.</summary>
    public IEnumerable<string> AllLog => _log.Entries.Select(e => e.Message);

    /// <summary>Everything logged at error level or above, by anything.</summary>
    public IEnumerable<string> Errors => _log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Stores the notices and chunks in a new database and returns its connection string.</summary>
    public static async Task<string> SeedAsync(PostgresFixture database, Notice[] notices, params DocumentToolTests.Chunk[] chunks)
    {
        var connection = await DocumentToolTests.SeedAsync(database, chunks);
        await using var dbContext = database.CreateDbContext(connection);
        dbContext.Notices.AddRange(notices.Select(n => NoticeEntity.Create(n, DateTimeOffset.UnixEpoch)));
        await dbContext.SaveChangesAsync(Ct);
        return connection;
    }

    public async Task<HttpResponseMessage> PostAsync(object? body, CancellationToken? cancellationToken = null)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/agent", body, cancellationToken ?? Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    /// <summary>Asks a question that must be answered with 200.</summary>
    public async Task<AgentResponse> AskAsync(string question = Question)
    {
        var response = await PostAsync(new { question });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentResponse>(Ct))!;
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private sealed class Log : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue((category, logLevel, $"{logLevel}: {formatter(state, exception)} {exception}"));
                }
            }
        }
    }
}
