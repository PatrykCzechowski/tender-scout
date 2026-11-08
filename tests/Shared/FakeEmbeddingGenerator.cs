using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using TenderScout.Domain.Documents;

namespace TenderScout.Tests.Shared;

/// <summary>
/// Stands in for the real model: the same text always gives the same 1024-long vector, with no network involved.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The texts of every call, in order.</summary>
    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>One-based call that throws, simulating an unreachable server.</summary>
    public int? FailOnCall { get; set; }

    /// <summary>Lets a test damage the output, e.g. drop a vector or change its length.</summary>
    public Func<IReadOnlyList<float[]>, IReadOnlyList<float[]>> Transform { get; set; } = vectors => vectors;

    public static float[] VectorFor(string text)
    {
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var random = new Random(seed);
        var vector = new float[ChunkEmbedding.Dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (random.NextSingle() * 2) - 1;
        }

        return vector;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var texts = values.ToList();
        Calls.Add(texts);
        if (Calls.Count == FailOnCall)
        {
            throw new HttpRequestException("Connection refused (simulated)");
        }

        var vectors = Transform([.. texts.Select(VectorFor)]);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(vectors.Select(v => new Embedding<float>(v))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
