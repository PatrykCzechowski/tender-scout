using MassTransit;
using TenderScout.Contracts.Documents;

namespace TenderScout.Infrastructure.Embeddings;

/// <summary>
/// Embeds the chunks of one document. Safe to run any number of times: only chunks without a vector
/// from the configured model are sent to the generator.
/// </summary>
public sealed class ChunksCreatedConsumer(ChunkEmbedder embedder) : IConsumer<ChunksCreated>
{
    public Task Consume(ConsumeContext<ChunksCreated> context) =>
        embedder.RunForDocumentAsync(context.Message.DocId, context.CancellationToken);
}
