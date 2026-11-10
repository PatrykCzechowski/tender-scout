using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace TenderScout.Infrastructure.Embeddings;

/// <summary>
/// Endpoint <c>chunks-created</c>, one message at a time because there is one GPU: retries embedding and transient
/// database failures with exponential backoff, then moves the message to <c>chunks-created_error</c>.
/// </summary>
public sealed class ChunksCreatedConsumerDefinition : ConsumerDefinition<ChunksCreatedConsumer>
{
    private const int RetryLimit = 5;

    private static readonly TimeSpan RetryMinInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryMaxInterval = TimeSpan.FromSeconds(30);

    public ChunksCreatedConsumerDefinition()
    {
        ConcurrentMessageLimit = 1;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ChunksCreatedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Exponential(RetryLimit, RetryMinInterval, RetryMaxInterval, RetryMinInterval);

            retry.Handle<ChunkEmbeddingException>();
            retry.Handle<TimeoutException>();
            retry.Handle<NpgsqlException>(ex => ex.IsTransient);
            retry.Handle<DbUpdateException>(ex => ex.InnerException is NpgsqlException { IsTransient: true });
            retry.Handle<RetryLimitExceededException>();
        });
    }
}
