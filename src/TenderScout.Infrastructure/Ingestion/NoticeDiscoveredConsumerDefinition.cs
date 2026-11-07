using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TenderScout.Infrastructure.Ingestion;

/// <summary>
/// Endpoint <c>notice-discovered</c>: retries transient failures with exponential backoff; anything else,
/// or a message still failing after the last retry, is moved to <c>notice-discovered_error</c>.
/// </summary>
public sealed class NoticeDiscoveredConsumerDefinition : ConsumerDefinition<NoticeDiscoveredConsumer>
{
    private readonly NoticeIngestionOptions _options;

    public NoticeDiscoveredConsumerDefinition(IOptions<NoticeIngestionOptions> options)
    {
        _options = options.Value;

        // Bounded so a burst of messages cannot exhaust the database connection pool.
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<NoticeDiscoveredConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            var delta = TimeSpan.FromTicks(Math.Max(_options.RetryMinInterval.Ticks, 1));
            retry.Exponential(_options.RetryLimit, _options.RetryMinInterval, _options.RetryMaxInterval, delta);

            // Only failures that may succeed on a later attempt are retried; a bug or bad message
            // goes to the error queue straight away instead of burning through retries.
            retry.Handle<TimeoutException>();
            retry.Handle<NpgsqlException>(ex => ex.IsTransient);
            retry.Handle<DbUpdateException>(ex => ex.InnerException is NpgsqlException { IsTransient: true });
            retry.Handle<RetryLimitExceededException>();
        });
    }
}
