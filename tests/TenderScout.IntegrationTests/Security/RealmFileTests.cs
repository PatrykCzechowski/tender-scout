using System.Text.Json;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.IntegrationTests.Security;

/// <summary>
/// The realm the local Keycloak imports, read as data. That Keycloak accepts the file and issues tokens the
/// services accept is checked by <c>scripts/check-images.sh</c>, not here.
/// </summary>
public sealed class RealmFileTests
{
    private static readonly JsonElement Realm =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Security", "tender-scout-realm.json"))).RootElement;

    private static readonly string[] BothScopes = [Scopes.NoticesRead, Scopes.DocumentsAsk];

    [Fact]
    public void Realm_HasTheTwoScopes_EachWithTheSharedAudience_AndNothingElseToGrant()
    {
        var scopes = Realm.GetProperty("clientScopes").EnumerateArray().ToList();

        Assert.Equal(BothScopes, scopes.Select(scope => scope.GetProperty("name").GetString()));
        Assert.All(scopes, scope => Assert.Equal(
            "tender-scout-api",
            Assert.Single(scope.GetProperty("protocolMappers").EnumerateArray()).GetProperty("config").GetProperty("included.custom.audience").GetString()));
        Assert.Equal(300, Realm.GetProperty("accessTokenLifespan").GetInt32());
        Assert.False(Realm.GetProperty("registrationAllowed").GetBoolean());
        Assert.All(["roles", "groups", "identityProviders"], name => Assert.False(Realm.TryGetProperty(name, out _), name));
    }

    [Fact]
    public void PageClient_IsPublic_SignsInWithACodeAndPkceOnly_AndReturnsToThePagesTwoLocalAddresses()
    {
        var client = Client("tender-scout-web");

        Assert.True(client.GetProperty("publicClient").GetBoolean());
        Assert.False(client.TryGetProperty("secret", out _));
        Assert.Equal((true, false, false, false), Flows(client));
        Assert.Equal("S256", client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        Assert.Equal(["http://localhost:5150/", "https://localhost:7150/"], Texts(client, "redirectUris"));
        Assert.Equal(["http://localhost:5150", "https://localhost:7150"], Texts(client, "webOrigins"));
        Assert.Equal(BothScopes, Texts(client, "optionalClientScopes"));
        Assert.Empty(Texts(client, "defaultClientScopes"));
    }

    [Fact]
    public void MachineClient_IsConfidential_AndGetsTokensWithItsOwnCredentialsOnly()
    {
        var client = Client("tender-scout-machine");

        Assert.False(client.GetProperty("publicClient").GetBoolean());
        Assert.Equal((false, false, false, true), Flows(client));
        Assert.Equal(BothScopes, Texts(client, "optionalClientScopes"));
        Assert.Empty(Texts(client, "defaultClientScopes"));
        Assert.Contains("local-only", client.GetProperty("secret").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyUser_IsInvented_AndCanSignInAtOnce()
    {
        var user = Assert.Single(Realm.GetProperty("users").EnumerateArray());
        var password = Assert.Single(user.GetProperty("credentials").EnumerateArray());

        Assert.EndsWith("@example.com", user.GetProperty("email").GetString(), StringComparison.Ordinal);
        Assert.Empty(Texts(user, "requiredActions"));
        Assert.False(password.GetProperty("temporary").GetBoolean());
        Assert.Contains("local-only", password.GetProperty("value").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, Realm.GetProperty("clients").GetArrayLength());
    }

    private static JsonElement Client(string id) =>
        Realm.GetProperty("clients").EnumerateArray().Single(client => client.GetProperty("clientId").GetString() == id);

    /// <summary>Authorization code, implicit, password and client credentials, in that order.</summary>
    private static (bool Code, bool Implicit, bool Password, bool ClientCredentials) Flows(JsonElement client) => (
        client.GetProperty("standardFlowEnabled").GetBoolean(),
        client.GetProperty("implicitFlowEnabled").GetBoolean(),
        client.GetProperty("directAccessGrantsEnabled").GetBoolean(),
        client.GetProperty("serviceAccountsEnabled").GetBoolean());

    private static IEnumerable<string?> Texts(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(value => value.GetString());
}
