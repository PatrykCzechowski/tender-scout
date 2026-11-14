using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace TenderScout.Infrastructure.Answering;

public static class AnsweringServiceCollectionExtensions
{
    internal const string HttpClientName = "chat";

    /// <summary>A switched-off machine should fail in seconds, while an answer may take a minute.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Registers <see cref="QuestionAnswerer"/>, its options (<c>Chat</c> section) and the <see cref="IChatClient"/> of
    /// the configured provider: Ollama, or OpenRouter through its OpenAI-compatible API. Needs <c>AddChunkEmbeddings</c>
    /// for the search side, which stays on Ollama either way.
    /// </summary>
    public static IHttpClientBuilder AddQuestionAnswering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ChatModelOptions>()
            .BindConfiguration(ChatModelOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) => options.ApiKey = configuration[ChatModelOptions.ApiKeySetting])
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ChatModelOptions>, ChatModelOptionsValidator>();

        services.AddScoped<QuestionAnswerer>();

        services.AddSingleton<IChatClient>(serviceProvider =>
        {
            var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var options = serviceProvider.GetRequiredService<IOptions<ChatModelOptions>>().Value;
            if (!options.IsOpenRouter)
            {
                return new OllamaApiClient(httpClient, options.Model);
            }

            // The SDK would repeat a failed request on its own, and each attempt is paid; the caller gets a 503 instead.
            var openRouter = new OpenAIClient(new ApiKeyCredential(options.ApiKey!), new OpenAIClientOptions
            {
                Endpoint = new Uri(options.Endpoint),
                Transport = new HttpClientPipelineTransport(httpClient),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                NetworkTimeout = options.RequestTimeout,
            });
            return new ProviderErrorsWithoutBody(openRouter.GetChatClient(options.Model).AsIChatClient());
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
