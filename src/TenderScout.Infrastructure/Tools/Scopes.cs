namespace TenderScout.Infrastructure.Tools;

/// <summary>
/// The two scopes of an access token. Each is also the name of the authorization policy that requires it.
/// </summary>
public static class Scopes
{
    /// <summary>Reading the stored notices.</summary>
    public const string NoticesRead = "notices:read";

    /// <summary>Asking about the text of the tender documents.</summary>
    public const string DocumentsAsk = "documents:ask";
}
