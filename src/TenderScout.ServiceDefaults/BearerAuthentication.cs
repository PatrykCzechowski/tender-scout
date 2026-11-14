using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Access with a JWT bearer token of the configured issuer. The issuer and the audience come from the framework's
/// own settings under <c>Authentication:Schemes:Bearer</c>, so another identity provider needs no code change.
/// </summary>
public static class BearerAuthentication
{
    private const string Settings = "Authentication:Schemes:Bearer";

    /// <summary>
    /// Validates bearer tokens and adds one authorization policy for each scope, named as the scope. A service
    /// without an issuer or an audience in its settings does not start.
    /// </summary>
    public static TBuilder AddBearerAuthentication<TBuilder>(this TBuilder builder, params string[] scopes)
        where TBuilder : IHostApplicationBuilder
    {
        // A service that cannot tell a valid token from an invalid one must not start and then refuse everyone.
        var settings = builder.Configuration.GetSection(Settings);
        if (string.IsNullOrWhiteSpace(settings["Authority"]) && !settings.GetSection("SigningKeys").Exists())
        {
            throw new InvalidOperationException($"Set {Settings}:Authority to the address of the token issuer.");
        }

        if (string.IsNullOrWhiteSpace(settings["ValidAudiences:0"]))
        {
            throw new InvalidOperationException($"Set {Settings}:ValidAudiences:0 to the audience a token must have.");
        }

        builder.Services.AddAuthentication().AddJwtBearer(options =>
        {
            // Claims keep the names they have in the token, so the scopes are found under "scope".
            options.MapInboundClaims = false;

            // The settings say which issuer and audience are valid. That both are checked is not a setting.
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
        });

        var authorization = builder.Services.AddAuthorizationBuilder();
        foreach (var scope in scopes)
        {
            authorization.AddPolicy(scope, policy => policy.RequireAssertion(context => context.User.HasScope(scope)));
        }

        return builder;
    }

    /// <summary>
    /// Whether the token grants the scope. The <c>scope</c> claim is one text with the scopes separated by
    /// spaces, and a scope counts only as a whole entry.
    /// </summary>
    public static bool HasScope(this ClaimsPrincipal user, string scope)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.FindAll("scope").Any(claim => claim.Value.Split(' ').Contains(scope));
    }
}
