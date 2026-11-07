using System.Net;
using System.Text.Json;

namespace TenderScout.Infrastructure.Ted;

/// <summary>
/// Raised when the TED API rejects a request, keeps failing after retries, or returns an unusable response.
/// </summary>
public sealed class TedApiException : Exception
{
    public TedApiException()
    {
    }

    public TedApiException(string message)
        : base(message)
    {
    }

    public TedApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TedApiException(string message, HttpStatusCode statusCode, string? errorType = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
    }

    /// <summary>HTTP status returned by TED, when the failure came from an HTTP response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>TED error type (e.g. <c>QUERY_UNKNOWN_FIELD</c>), when TED reported one.</summary>
    public string? ErrorType { get; }

    internal static TedApiException FromErrorResponse(HttpStatusCode statusCode, string body)
    {
        string? detail = null;
        string? errorType = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    detail = message.GetString();
                }

                if (root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String)
                {
                    errorType = type.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Not a TED error payload; fall back to the status code alone.
        }

        var text = detail is null
            ? $"TED search failed with HTTP {(int)statusCode} ({statusCode})."
            : $"TED search failed with HTTP {(int)statusCode} ({statusCode}): {detail}";

        return new TedApiException(text, statusCode, errorType);
    }
}
