using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.IntegrationTests.Security;

/// <summary>
/// The token issuer of the tests. A host is given its name, audience and signing key through the framework's own
/// bearer settings, and the tests write tokens with that key, so every token goes through the real handler: the
/// signature, issuer, audience and lifetime are checked as for a token of a real issuer.
/// </summary>
internal static class TestIssuer
{
    public const string Issuer = "https://issuer.test";
    public const string Audience = "tender-scout-test";
    public const string BothScopes = $"{Scopes.NoticesRead} {Scopes.DocumentsAsk}";

    /// <summary>New in every test run.</summary>
    public static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static IWebHostBuilder UseTestIssuer(this IWebHostBuilder web, string issuer = Issuer, string audience = Audience, byte[]? key = null) => web
        .UseSetting("Authentication:Schemes:Bearer:ValidIssuer", issuer)
        .UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", audience)
        .UseSetting("Authentication:Schemes:Bearer:SigningKeys:0:Issuer", issuer)
        .UseSetting("Authentication:Schemes:Bearer:SigningKeys:0:Value", Convert.ToBase64String(key ?? Key));

    /// <summary>A token that is valid as it is; each argument changes one thing about it.</summary>
    /// <param name="scope">The text of the <c>scope</c> claim.</param>
    /// <param name="key">The signing key; <see langword="null"/> signs with the key the hosts trust.</param>
    /// <param name="signed">False writes a token without a signature.</param>
    /// <param name="validFromMinutes">When the token becomes valid, in minutes from now.</param>
    /// <param name="validForMinutes">How long it then stays valid.</param>
    public static string Token(
        string scope = BothScopes,
        string issuer = Issuer,
        string audience = Audience,
        byte[]? key = null,
        bool signed = true,
        int validFromMinutes = -1,
        int validForMinutes = 10)
    {
        var validFrom = DateTime.UtcNow.AddMinutes(validFromMinutes);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["scope"] = scope },
            IssuedAt = validFrom,
            NotBefore = validFrom,
            Expires = validFrom.AddMinutes(validForMinutes),
            SigningCredentials = signed ? new SigningCredentials(new SymmetricSecurityKey(key ?? Key), SecurityAlgorithms.HmacSha256) : null,
        });
    }

    /// <summary>Sends the token with every request of the client; without an argument, a valid token with both scopes.</summary>
    public static HttpClient WithToken(this HttpClient client, string? token = null)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token());
        return client;
    }
}
