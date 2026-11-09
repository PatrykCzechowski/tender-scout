using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Infrastructure.Documents;

public static class DocumentImportServiceCollectionExtensions
{
    /// <summary>Registers <see cref="DocumentImporter"/>, its repository and options (<c>DocumentImport</c> section).</summary>
    public static IServiceCollection AddDocumentImport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DocumentImportOptions>()
            .BindConfiguration(DocumentImportOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DocumentImportOptions>, DocumentImportOptionsValidator>();

        services.AddDocumentProcessing();
        services.AddScoped<DocumentImporter>();

        return services;
    }

    /// <summary>Registers the document repository and chunking options (<c>DocumentChunking</c> section).</summary>
    public static IServiceCollection AddDocumentProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DocumentChunkingOptions>()
            .BindConfiguration(DocumentChunkingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DocumentChunkingOptions>, DocumentChunkingOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDocumentRepository, DocumentRepository>();

        return services;
    }

    /// <summary>Adds the consumers that process uploaded documents.</summary>
    public static IBusRegistrationConfigurator AddDocumentConsumers(this IBusRegistrationConfigurator bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        bus.AddConsumer<DocumentUploadedConsumer, DocumentUploadedConsumerDefinition>();
        return bus;
    }
}
