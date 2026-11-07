using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TenderScout.Infrastructure.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    /// <summary>Registers <see cref="NoticeIngestion"/> and its options (<c>Ingestion</c> section).</summary>
    public static IServiceCollection AddNoticeIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<NoticeIngestionOptions>()
            .BindConfiguration(NoticeIngestionOptions.SectionName)
            .Validate(options => options.IsValid(), $"Invalid '{NoticeIngestionOptions.SectionName}' configuration.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<NoticeIngestion>();

        return services;
    }

    /// <summary>Adds the consumers that store discovered notices.</summary>
    public static IBusRegistrationConfigurator AddNoticeIngestionConsumers(this IBusRegistrationConfigurator bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        bus.AddConsumer<NoticeDiscoveredConsumer, NoticeDiscoveredConsumerDefinition>();
        return bus;
    }

    /// <summary>
    /// Uses RabbitMQ at <paramref name="host"/> (an <c>amqp://</c> URI, as Aspire provides it) with
    /// kebab-case queue names, and creates a receive endpoint for every registered consumer.
    /// </summary>
    public static IBusRegistrationConfigurator UsingTenderScoutRabbitMq(this IBusRegistrationConfigurator bus, Uri host)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(host);

        bus.SetKebabCaseEndpointNameFormatter();
        bus.UsingRabbitMq((context, rabbit) =>
        {
            rabbit.Host(host);
            rabbit.ConfigureEndpoints(context);
        });

        return bus;
    }
}
