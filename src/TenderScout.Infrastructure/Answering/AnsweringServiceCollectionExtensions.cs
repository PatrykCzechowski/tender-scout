using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace TenderScout.Infrastructure.Answering;

public static class AnsweringServiceCollectionExtensions
{
    internal const string HttpClientName = "ollama-chat";

    /// <summary>A switched-off machine should fail in seconds, while an answer may take a minute.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Registers <see cref="QuestionAnswerer"/>, its options (<c>Chat</c> section) and the Ollama-backed
    /// <see cref="IChatClient"/>. Needs <c>AddChunkEmbeddings</c> for the search side.
    /// </summary>
    public static IHttpClientBuilder AddQuestionAnswering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ChatModelOptions>()
            .BindConfiguration(ChatModelOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ChatModelOptions>, ChatModelOptionsValidator>();

        services.AddScoped<QuestionAnswerer>();

        services.AddSingleton<IChatClient>(serviceProvider =>
        {
            var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var options = serviceProvider.GetRequiredService<IOptions<ChatModelOptions>>().Value;
            return new OllamaApiClient(httpClient, options.Model);
        });

        var builder = services.AddHttpClient(HttpClientName, (serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ChatModelOptions>>().Value;
            httpClient.BaseAddress = new Uri(options.Endpoint);
            httpClient.Timeout = options.RequestTimeout;
        });

        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = ConnectTimeout });

        // Service defaults limit every HttpClient to 10 s per attempt and 30 s in total, too short for an answer.
        // No retry replaces them: a repeated generation would keep the caller waiting for minutes.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental.
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        return builder;
    }
}
