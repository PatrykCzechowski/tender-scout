using System.Net;
using System.Text;

namespace TenderScout.UnitTests.Ted;

/// <summary>
/// Replays scripted responses instead of calling TED, and records what the client sent.
/// </summary>
internal sealed class FakeTedHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeTedHandler RespondWithFixture(string fixture, HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(_ => Task.FromResult(Json(status, Fixtures.Read(fixture))));

    public FakeTedHandler RespondWithStatus(HttpStatusCode status) =>
        Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)));

    /// <summary>Never answers; the request only ends when the client cancels it (e.g. attempt timeout).</summary>
    public FakeTedHandler Hang() =>
        Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri, body));

        if (!_responses.TryDequeue(out var respond))
        {
            throw new InvalidOperationException($"Unexpected request #{Requests.Count} to {request.RequestUri}.");
        }

        return await respond(cancellationToken);
    }

    private FakeTedHandler Enqueue(Func<CancellationToken, Task<HttpResponseMessage>> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Body);

internal static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ted", name));
}
