using Microsoft.Extensions.Options;

namespace TenderScout.Infrastructure.Documents;

/// <summary>Reports every invalid setting by its configuration key, so the import fails on start with a usable message.</summary>
internal sealed class DocumentImportOptionsValidator : IValidateOptions<DocumentImportOptions>
{
    private const string Prefix = DocumentImportOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, DocumentImportOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DocumentsFolder))
        {
            failures.Add($"{Prefix}{nameof(options.DocumentsFolder)} is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ManifestPath))
        {
            failures.Add($"{Prefix}{nameof(options.ManifestPath)} is required.");
        }

        if (options.DocKinds.Count == 0 || options.DocKinds.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{Prefix}{nameof(options.DocKinds)} must list at least one document kind.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
