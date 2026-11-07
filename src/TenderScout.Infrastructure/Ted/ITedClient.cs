using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Ted;

/// <summary>
/// Reads public IT tenders from the TED (Tenders Electronic Daily) API v3.
/// </summary>
public interface ITedClient
{
    /// <summary>
    /// Returns all notices published by Polish buyers with a main CPV code in <c>72*</c> or <c>48*</c>
    /// between <paramref name="from"/> and <paramref name="to"/> (both inclusive), reading every result page.
    /// </summary>
    /// <exception cref="TedApiException">TED rejected the request or kept failing after retries.</exception>
    Task<IReadOnlyList<Notice>> SearchNoticesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
