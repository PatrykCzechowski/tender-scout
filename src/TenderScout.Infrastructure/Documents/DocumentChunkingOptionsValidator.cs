using Microsoft.Extensions.Options;

namespace TenderScout.Infrastructure.Documents;

/// <summary>Reports every invalid setting by its configuration key, so the host fails on start with a usable message.</summary>
internal sealed class DocumentChunkingOptionsValidator : IValidateOptions<DocumentChunkingOptions>
{
    private const string Prefix = DocumentChunkingOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, DocumentChunkingOptions options)
    {
        var failures = new List<string>();

        if (options.ChunkSize <= 0)
        {
            failures.Add($"{Prefix}{nameof(options.ChunkSize)} must be positive.");
        }

        if (options.ChunkOverlap < 0)
        {
            failures.Add($"{Prefix}{nameof(options.ChunkOverlap)} must not be negative.");
        }
        else if (options.ChunkOverlap >= options.ChunkSize)
        {
            failures.Add($"{Prefix}{nameof(options.ChunkOverlap)} must be smaller than {nameof(options.ChunkSize)}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
