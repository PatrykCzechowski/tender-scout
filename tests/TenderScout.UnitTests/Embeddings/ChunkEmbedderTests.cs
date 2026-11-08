using Microsoft.Extensions.AI;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Tests.Shared;

namespace TenderScout.UnitTests.Embeddings;

public sealed class ChunkEmbedderTests
{
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
}
