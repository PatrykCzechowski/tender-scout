using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Embeddings;

public sealed class ChunkEmbedderTests
{
    private const string Model = "bge-m3";

    private readonly FakeEmbeddingGenerator _generator = new();
    private readonly FakeChunkRepository _repository = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FakeGenerator_SameText_GivesTheSameVector()
    {
        Assert.Equal(FakeEmbeddingGenerator.VectorFor("Pierwszy fragment"), FakeEmbeddingGenerator.VectorFor("Pierwszy fragment"));
        Assert.NotEqual(FakeEmbeddingGenerator.VectorFor("Pierwszy fragment"), FakeEmbeddingGenerator.VectorFor("Drugi fragment"));
        Assert.Equal(ChunkEmbedding.Dimensions, FakeEmbeddingGenerator.VectorFor("Pierwszy fragment").Length);
    }

    [Fact]
    public void ProviderTypes_StayOutOfDomainAndEmbedder()
    {
        var domainReferences = typeof(TenderDocument).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        var embedderParameters = typeof(ChunkEmbedder).GetConstructors().Single().GetParameters().Select(p => p.ParameterType);

        Assert.DoesNotContain(domainReferences, name => name is "OllamaSharp" or "Pgvector" or "Microsoft.Extensions.AI.Abstractions");
        Assert.Contains(typeof(IEmbeddingGenerator<string, Embedding<float>>), embedderParameters);
        Assert.DoesNotContain(embedderParameters, type => type.Namespace?.StartsWith("OllamaSharp", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Run_SplitsChunksIntoConfiguredBatchesInOrder()
    {
        _repository.Add(7);

        var report = await RunAsync(batchSize: 3);

        Assert.Equal(new ChunkEmbeddingReport(7, 3), report);
        Assert.Equal([3, 3, 1], _generator.Calls.Select(call => call.Count));
        Assert.Equal(_repository.Texts, _generator.Calls.SelectMany(call => call));
    }

    [Fact]
    public async Task Run_StoresGeneratedVectorsWithTheModelName()
    {
        _repository.Add(2);

        await RunAsync();

        Assert.All(_repository.Chunks, chunk =>
        {
            Assert.Equal(Model, chunk.Model);
            Assert.Equal(FakeEmbeddingGenerator.VectorFor(chunk.Text), chunk.Vector);
        });
    }

    [Fact]
    public async Task Run_NothingPending_NeverCallsTheGenerator()
    {
        var report = await RunAsync();

        Assert.Equal(new ChunkEmbeddingReport(0, 0), report);
        Assert.Empty(_generator.Calls);
    }

    [Fact]
    public async Task Run_TooFewVectors_StoresNothingFromThatBatch()
    {
        _repository.Add(3);
        _generator.Transform = vectors => [.. vectors.Take(2)];

        var exception = await Assert.ThrowsAsync<ChunkEmbeddingException>(() => RunAsync());

        Assert.Contains("expected 3 vectors, got 2", exception.Message, StringComparison.Ordinal);
        Assert.All(_repository.Chunks, chunk => Assert.Null(chunk.Vector));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1536)]
    public async Task Run_WrongDimension_NamesBothLengths(int dimensions)
    {
        _repository.Add(2);
        _generator.Transform = vectors => [.. vectors.Select(_ => new float[dimensions])];

        var exception = await Assert.ThrowsAsync<ChunkEmbeddingException>(() => RunAsync());

        Assert.Contains($"expected 1024 dimensions, got {dimensions}", exception.Message, StringComparison.Ordinal);
        Assert.All(_repository.Chunks, chunk => Assert.Null(chunk.Vector));
    }

    [Fact]
    public async Task Run_GeneratorFails_KeepsEarlierBatchesAndStops()
    {
        _repository.Add(6);
        _generator.FailOnCall = 2;

        var exception = await Assert.ThrowsAsync<ChunkEmbeddingException>(() => RunAsync(batchSize: 2));

        Assert.Contains("Batch 2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Connection refused", exception.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(2, _generator.Calls.Count);
        Assert.Equal(2, _repository.Chunks.Count(chunk => chunk.Vector is not null));
    }

    [Fact]
    public async Task Run_Cancelled_IsNotReportedAsAGeneratorFailure()
    {
        _repository.Add(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(cancellationToken: new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Run_ChunksVanishingBeforeTheirWrite_StillFinishes()
    {
        _repository.Add(4);
        _repository.IgnoreSaves = true;

        var report = await RunAsync(batchSize: 2);

        Assert.Equal(2, report.Batches);
    }

    private Task<ChunkEmbeddingReport> RunAsync(int batchSize = 32, CancellationToken? cancellationToken = null)
    {
        var options = Options.Create(new EmbeddingOptions { Endpoint = "http://ollama.test:11434", Model = Model, BatchSize = batchSize });
        return new ChunkEmbedder(_generator, _repository, options, NullLogger<ChunkEmbedder>.Instance).RunAsync(cancellationToken ?? Ct);
    }

    private sealed class FakeChunkRepository : IChunkEmbeddingRepository
    {
        public List<StoredChunk> Chunks { get; } = [];

        public bool IgnoreSaves { get; set; }

        public IEnumerable<string> Texts => Chunks.Select(chunk => chunk.Text);

        public void Add(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Chunks.Add(new StoredChunk(Chunks.Count + 1, $"Fragment {Chunks.Count + 1}"));
            }
        }

        public Task<IReadOnlyList<ChunkToEmbed>> GetPendingAsync(string model, long afterChunkId, int limit, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ChunkToEmbed> pending =
            [
                .. Chunks.Where(chunk => chunk.Id > afterChunkId && chunk.Model != model)
                    .Take(limit)
                    .Select(chunk => new ChunkToEmbed(chunk.Id, chunk.Text)),
            ];
            return Task.FromResult(pending);
        }

        public Task SaveAsync(string model, IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken = default)
        {
            if (!IgnoreSaves)
            {
                foreach (var embedding in embeddings)
                {
                    var chunk = Chunks.Single(c => c.Id == embedding.ChunkId);
                    chunk.Vector = embedding.Vector.ToArray();
                    chunk.Model = model;
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(ReadOnlyMemory<float> queryVector, string model, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StoredChunk(long id, string text)
    {
        public long Id { get; } = id;

        public string Text { get; } = text;

        public float[]? Vector { get; set; }

        public string? Model { get; set; }
    }
}
