using System.ClientModel;
using System.Net;
using Microsoft.Extensions.AI;

namespace TenderScout.Infrastructure.Answering;

/// <summary>
/// Turns an error of the OpenAI client into an <see cref="HttpRequestException"/> that holds only the status. The
/// client's own exception quotes the provider's answer, which may echo the request or the key, and the API logs the
/// exception when it answers 503. Streaming is not used, so only <see cref="GetResponseAsync"/> is wrapped.
/// </summary>
internal sealed class ProviderErrorsWithoutBody(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientResultException exception)
        {
            // Status 0: no answer at all; the inner exception is the connection error, which holds no provider text.
            throw exception.Status == 0
                ? new HttpRequestException("The chat provider could not be reached.", exception.InnerException)
                : new HttpRequestException($"The chat provider answered HTTP {exception.Status}.", null, (HttpStatusCode)exception.Status);
        }
    }
}
