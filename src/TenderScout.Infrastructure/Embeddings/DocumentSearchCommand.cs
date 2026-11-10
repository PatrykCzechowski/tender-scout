using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TenderScout.Domain.Documents;
using TenderScout.Infrastructure.Persistence;

namespace TenderScout.Infrastructure.Embeddings;

/// <summary>
/// Development command: embeds a question with the configured model and prints the best passages from hybrid search.
/// </summary>
public sealed class DocumentSearchCommand(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    ChunkEmbeddingRepository repository,
    IOptions<EmbeddingOptions> options)
{
    private const int ResultCount = 5;
    private const int Failed = 1;
    private const int Succeeded = 0;

    /// <returns>The exit code of the command.</returns>
    public async Task<int> RunAsync(string query, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (string.IsNullOrWhiteSpace(query))
        {
            await error.WriteLineAsync("Usage: dotnet run --project src/TenderScout.DocumentSearch -- \"your question\"").ConfigureAwait(false);
            return Failed;
        }

        IReadOnlyList<ChunkSearchResult> results;
        try
        {
            results = await SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync($"Search failed: {ex.Message}").ConfigureAwait(false);
            return Failed;
        }

        if (results.Count == 0)
        {
            await output.WriteLineAsync("No matching passages found.").ConfigureAwait(false);
            return Succeeded;
        }

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var heading = string.Create(CultureInfo.InvariantCulture, $"{i + 1}. score {result.Score:F4} | {result.DocId} | page {result.PageNumber}");
            await output.WriteLineAsync(heading).ConfigureAwait(false);
            await output.WriteLineAsync(result.Text).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
        }

        return Succeeded;
    }

    private async Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var vector = await generator.GenerateVectorAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await repository.HybridSearchAsync(query, vector, settings.Model, ResultCount, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
