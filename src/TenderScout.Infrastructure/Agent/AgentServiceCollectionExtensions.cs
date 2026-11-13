using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.Infrastructure.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TenderAgent"/>, its options (<c>Agent</c> section) and the tools it calls in process.
    /// Needs <c>AddChunkEmbeddings</c> and <c>AddQuestionAnswering</c> for the embedding and chat clients, and a
    /// registered <see cref="Persistence.NoticeQueries"/>.
    /// </summary>
    public static IServiceCollection AddTenderAgent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AgentOptions>().BindConfiguration(AgentOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TenderTools>();
        services.AddScoped<TenderAgent>();

        return services;
    }
}
