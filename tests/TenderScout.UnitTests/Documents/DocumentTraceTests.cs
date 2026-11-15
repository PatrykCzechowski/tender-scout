using System.Diagnostics;
using MassTransit;
using MassTransit.Logging;
using MassTransit.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using TenderScout.Contracts.Documents;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Documents;

/// <summary>
/// The trace of one document across the queue, with the services' telemetry settings, MassTransit's in-memory transport
/// and the real embedding client on a fake Ollama. The two consumers stand in for the Worker's at the database and file
/// boundary: the first publishes ChunksCreated, the second embeds. No broker, database or model is involved.
/// </summary>
public sealed class DocumentTraceTests
{
    private const string Secret = "Jan Kowalski, PESEL 85010112345";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Document_IsOneTrace_FromThePublishThroughBothConsumersToTheEmbeddings()
    {
        var trace = await PublishAsync("swz.pdf");

        var publish = Span(trace, ActivityKind.Producer, "DocumentUploaded send");
        var uploadReceive = Span(trace, ActivityKind.Consumer, "UploadStep receive");
        var uploadProcess = Span(trace, ActivityKind.Consumer, "UploadStep process");
        var chunksPublish = Span(trace, ActivityKind.Producer, "ChunksCreated send");
        var embeddingReceive = Span(trace, ActivityKind.Consumer, "EmbeddingStep receive");
        var embeddingProcess = Span(trace, ActivityKind.Consumer, "EmbeddingStep process");
        var embeddings = Span(trace, ActivityKind.Client, "embeddings bge-m3");

        // Nothing was current when the test published, so the publish starts the trace; each receive takes its parent
        // from the message's headers.
        Assert.Equal(default, publish.ParentSpanId);
        Assert.Equal(publish.SpanId, uploadReceive.ParentSpanId);
        Assert.Equal(uploadReceive.SpanId, uploadProcess.ParentSpanId);
        Assert.Equal(uploadProcess.SpanId, chunksPublish.ParentSpanId);
        Assert.Equal(chunksPublish.SpanId, embeddingReceive.ParentSpanId);
        Assert.Equal(embeddingReceive.SpanId, embeddingProcess.ParentSpanId);
        Assert.Equal(embeddingProcess.SpanId, embeddings.ParentSpanId);
        Assert.Equal(FakeOllamaHandler.PromptTokens, embeddings.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.DoesNotContain(trace, span => Exported(span).Contains("Fragment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailingConsumer_KeepsItsErrorStatusAndType_WithoutTheExceptionsTextInTheStatus()
    {
        var trace = await PublishAsync("broken.pdf");

        var process = Span(trace, ActivityKind.Consumer, "UploadStep process");
        Assert.Equal(ActivityStatusCode.Error, process.Status);
        Assert.Null(process.StatusDescription);
        var exception = Assert.Single(process.Events, e => e.Name == "exception");
        Assert.Contains(exception.Tags, t => t.Key == "exception.type" && (string?)t.Value == typeof(InvalidOperationException).FullName);

        // MassTransit also copies the message and the stack trace into that event, whose tags cannot be changed after
        // it is created; only the tags and status of the spans are free of them.
        Assert.DoesNotContain(trace, span => string.Join('\n', [span.StatusDescription, .. span.Tags.Select(t => $"{t.Value}")])
            .Contains("85010112345", StringComparison.Ordinal));
    }

    private static Activity Span(List<Activity> trace, ActivityKind kind, string nameEnd) =>
        Assert.Single(trace, s => s.Kind == kind && s.DisplayName.EndsWith(nameEnd, StringComparison.Ordinal));

    /// <summary>Every name, tag, event and status text of the span, as an exporter would see them.</summary>
    private static string Exported(Activity span) => string.Join(
        '\n',
        [span.DisplayName, span.StatusDescription, .. span.Tags.Select(t => $"{t.Key}={t.Value}"),
         .. span.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}:{t.Key}={t.Value}"))]);

    /// <summary>Publishes a document's message as DocumentImport does and returns its trace once the second consumer ended.</summary>
    private static async Task<List<Activity>> PublishAsync(string path)
    {
        List<Activity> spans = [];
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embedding:Endpoint"] = "http://ollama.test:11434",
            ["Embedding:Model"] = "bge-m3",
        });
        builder.AddServiceDefaults();
        builder.Services.AddChunkEmbeddings().ConfigurePrimaryHttpMessageHandler(() => new FakeOllamaHandler());
        builder.Services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<UploadStep>();
            bus.AddConsumer<EmbeddingStep>();
        });

        // As in the Worker and DocumentImport.
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource(DiagnosticHeaders.DefaultListenerName)
            .AddInMemoryExporter(spans));

        using var host = builder.Build();
        var harness = host.Services.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = NewId.NextGuid();
        await harness.Bus.Publish(new DocumentUploaded("doc-1", "100001-2026", "swz", path, $"/documents/{path}"), context => context.MessageId = messageId, Ct);

        var tracing = host.Services.GetRequiredService<TracerProvider>();
        for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(30); await Task.Delay(50, Ct))
        {
            tracing.ForceFlush();
            List<Activity> trace;
            lock (spans)
            {
                var publish = spans.FirstOrDefault(s => s.Kind == ActivityKind.Producer
                    && (s.GetTagItem("messaging.masstransit.message_id") as string) == messageId.ToString());
                trace = publish is null ? [] : [.. spans.Where(s => s.TraceId == publish.TraceId)];
            }

            // A span is exported when it ends, after the spans below it: the second consumer's receive span ends last.
            if (trace.Any(s => s.DisplayName.EndsWith("EmbeddingStep receive", StringComparison.Ordinal)
                || (s.DisplayName.EndsWith("UploadStep process", StringComparison.Ordinal) && s.Status == ActivityStatusCode.Error)))
            {
                return trace;
            }
        }

        throw new TimeoutException("The document's trace did not reach its second consumer within 30 s.");
    }

    private sealed class UploadStep : IConsumer<DocumentUploaded>
    {
        public async Task Consume(ConsumeContext<DocumentUploaded> context)
        {
            if (context.Message.Path == "broken.pdf")
            {
                throw new InvalidOperationException($"Could not read /documents/{Secret}.pdf");
            }

            await context.Publish(new ChunksCreated(context.Message.DocId), context.CancellationToken);
        }
    }

    private sealed class EmbeddingStep(IEmbeddingGenerator<string, Embedding<float>> generator) : IConsumer<ChunksCreated>
    {
        public Task Consume(ConsumeContext<ChunksCreated> context) =>
            generator.GenerateAsync(["Fragment pierwszy", "Fragment drugi"], cancellationToken: context.CancellationToken);
    }
}
