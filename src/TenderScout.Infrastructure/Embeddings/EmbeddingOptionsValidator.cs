using Microsoft.Extensions.Options;

namespace TenderScout.Infrastructure.Embeddings;

/// <summary>Checks configuration only, without contacting the endpoint, and names every invalid key.</summary>
internal sealed class EmbeddingOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    /// <summary>The resilience pipeline rejects a shorter attempt timeout.</summary>
    private static readonly TimeSpan MinRequestTimeout = TimeSpan.FromMilliseconds(10);

    private const string Prefix = EmbeddingOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        var failures = new List<string>();

        if (!IsHttpUri(options.Endpoint))
        {
            failures.Add($"{Prefix}{nameof(options.Endpoint)} must be an absolute http or https address.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add($"{Prefix}{nameof(options.Model)} is required.");
        }

        if (options.BatchSize <= 0)
        {
            failures.Add($"{Prefix}{nameof(options.BatchSize)} must be positive.");
        }

        if (options.RequestTimeout < MinRequestTimeout)
        {
            failures.Add($"{Prefix}{nameof(options.RequestTimeout)} must be at least {MinRequestTimeout.TotalMilliseconds} ms.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
