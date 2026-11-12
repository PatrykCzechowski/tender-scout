using Microsoft.Extensions.Options;

namespace TenderScout.Infrastructure.Answering;

/// <summary>Checks configuration only, without contacting the endpoint, and names every invalid key.</summary>
internal sealed class ChatModelOptionsValidator : IValidateOptions<ChatModelOptions>
{
    private const string Prefix = ChatModelOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, ChatModelOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{Prefix}{nameof(options.Endpoint)} must be an absolute http or https address.");
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
