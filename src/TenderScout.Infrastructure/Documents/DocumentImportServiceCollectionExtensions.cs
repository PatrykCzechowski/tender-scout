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

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<DocumentImporter>();

        return services;
    }
}
