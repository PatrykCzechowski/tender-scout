using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace TenderScout.Infrastructure.Documents;

/// <summary>
/// Endpoint <c>document-uploaded</c>, one message at a time: retries transient database failures with exponential
/// backoff. Anything else, such as a corrupted or missing file, goes to <c>document-uploaded_error</c> straight away.
/// </summary>
public sealed class DocumentUploadedConsumerDefinition : ConsumerDefinition<DocumentUploadedConsumer>
{
    private const int RetryLimit = 5;

    private static readonly TimeSpan RetryMinInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryMaxInterval = TimeSpan.FromSeconds(30);

    public DocumentUploadedConsumerDefinition()
    {
        ConcurrentMessageLimit = 1;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DocumentUploadedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Exponential(RetryLimit, RetryMinInterval, RetryMaxInterval, RetryMinInterval);

            retry.Handle<TimeoutException>();
            retry.Handle<NpgsqlException>(ex => ex.IsTransient);
            retry.Handle<DbUpdateException>(ex => ex.InnerException is NpgsqlException { IsTransient: true });
            retry.Handle<RetryLimitExceededException>();
        });
    }
}
