using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Ted;

/// <summary>
/// Typed <see cref="HttpClient"/> for the TED search API. Retries and timeouts live in the resilience
/// pipeline configured by <see cref="TedServiceCollectionExtensions.AddTedClient"/>.
/// </summary>
internal sealed partial class TedClient(HttpClient httpClient, IOptions<TedOptions> options, ILogger<TedClient> logger)
    : ITedClient
{
    internal const string SearchPath = "v3/notices/search";

    /// <summary>Search all notice versions, not only the currently active ones.</summary>
    private const string Scope = "ALL";

    /// <summary>Token-based paging; unlike page numbers it has no upper bound on how deep it can read.</summary>
    private const string PaginationMode = "ITERATION";

    public async Task<IReadOnlyList<Notice>> SearchNoticesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, $"End date must not be before start date {from:yyyy-MM-dd}.");
        }

        var pageSize = options.Value.PageSize;
        var query = TedSearchQuery.Build(from, to);
        var notices = new List<Notice>();
        string? nextToken = null;
        var page = 0;

        while (true)
        {
            page++;
            var request = new TedSearchRequest(query, TedFields.All, pageSize, Scope, PaginationMode, nextToken);
            var response = await SearchPageAsync(request, cancellationToken).ConfigureAwait(false);

            var pageNotices = response.Notices ?? [];
            notices.AddRange(pageNotices.Select(TedNoticeMapper.Map));
            LogPageRead(logger, page, pageNotices.Count, response.TotalNoticeCount);

            // TED keeps returning a token even on the last page, so a short page or reaching the
            // reported total also ends the iteration; this avoids one extra empty request.
            nextToken = response.IterationNextToken;
            var isLastPage = string.IsNullOrEmpty(nextToken)
                || pageNotices.Count < pageSize
                || (response.TotalNoticeCount is { } total && notices.Count >= total);

            if (isLastPage)
            {
                return notices;
            }
        }
    }

    private async Task<TedSearchResponse> SearchPageAsync(TedSearchRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .PostAsJsonAsync(SearchPath, request, TedJsonContext.Default.TedSearchRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException)
        {
            // Network failures and timeouts that outlived every retry, or an open circuit breaker.
            throw new TedApiException("TED search request failed after retries.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw TedApiException.FromErrorResponse(response.StatusCode, body);
            }

            TedSearchResponse? result;
            try
            {
                result = await response.Content
                    .ReadFromJsonAsync(TedJsonContext.Default.TedSearchResponse, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new TedApiException("TED returned a search response that could not be parsed.", ex);
            }

            if (result is null)
            {
                throw new TedApiException("TED returned an empty search response.");
            }

            if (result.TimedOut)
            {
                throw new TedApiException("TED search timed out on the server; the results would be incomplete.");
            }

            return result;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Read TED page {Page}: {Count} notices (total reported: {Total})")]
    private static partial void LogPageRead(ILogger logger, int page, int count, long? total);
}
