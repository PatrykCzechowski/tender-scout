using Microsoft.Extensions.Options;

namespace TenderScout.Infrastructure.Answering;

/// <summary>Checks configuration only, without contacting the endpoint, and names every invalid key.</summary>
internal sealed class ChatModelOptionsValidator : IValidateOptions<ChatModelOptions>
{
    private const string Prefix = ChatModelOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, ChatModelOptions options)
    {
        var failures = new List<string>();

        if (!options.IsOpenRouter && !string.Equals(options.Provider, ChatModelOptions.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{Prefix}{nameof(options.Provider)} must be {ChatModelOptions.Ollama} or {ChatModelOptions.OpenRouter}.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{Prefix}{nameof(options.Endpoint)} must be an absolute http or https address.");
        }
        else if (options.IsOpenRouter && (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0))
        {
            // The key travels in a header to this address, so it must be encrypted and the address must hold no credentials.
            failures.Add($"{Prefix}{nameof(options.Endpoint)} must be an https address without credentials for {ChatModelOptions.OpenRouter}.");
        }

        if (options.IsOpenRouter && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add($"{ChatModelOptions.ApiKeySetting} is required when {Prefix}{nameof(options.Provider)} is {ChatModelOptions.OpenRouter}.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add($"{Prefix}{nameof(options.Model)} is required.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{Prefix}{nameof(options.RequestTimeout)} must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
