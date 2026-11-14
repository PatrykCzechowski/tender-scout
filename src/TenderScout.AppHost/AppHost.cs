var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "pg17")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("tenderscout");

var rabbitmq = builder.AddRabbitMQ("messaging")
    .WithManagementPlugin()
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

// Keycloak issues the access tokens for local runs. It imports the realm file of the repository at every start
// and keeps nothing between starts, so a change in that file takes effect on the next start.
const string Issuer = "http://localhost:8180/realms/tender-scout";
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.8")
    .WithArgs("start-dev", "--import-realm")
    .WithBindMount(Path.Combine(builder.AppHostDirectory, "..", "..", "keycloak"), "/opt/keycloak/data/import", isReadOnly: true)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", builder.AddParameter("keycloak-admin-password", new GenerateParameterDefault { MinLength = 24 }, secret: true, persist: true))
    .WithHttpEndpoint(port: 8180, targetPort: 8080)
    .WithHttpHealthCheck("/realms/tender-scout");

// Applies EF Core migrations and exits; the services below start only after it succeeds.
var migrations = builder.AddProject<Projects.TenderScout_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database);

builder.AddProject<Projects.TenderScout_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WaitForCompletion(migrations)
    .WithTokenIssuer(keycloak, Issuer)
    .WithEnvironment("WebSignIn__ClientId", "tender-scout-web")
    .WithHttpHealthCheck("/health");

// MCP server: the notice and document tools for MCP clients, at /mcp.
builder.AddProject<Projects.TenderScout_Mcp>("mcp")
    .WithReference(database)
    .WaitFor(database)
    .WaitForCompletion(migrations)
    .WithTokenIssuer(keycloak, Issuer)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TenderScout_Worker>("worker")
    .WithReference(database)
    .WithReference(rabbitmq)
    .WaitFor(database)
    .WaitFor(rabbitmq)
    .WaitForCompletion(migrations);

// Developer command: starts only from the dashboard, so a long import never delays the services above.
builder.AddProject<Projects.TenderScout_DocumentImport>("document-import")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq)
    .WithExplicitStart();

builder.Build().Run();

internal static class TokenIssuer
{
    /// <summary>
    /// Gives a service the issuer and the audience of the tokens it accepts, and starts it after the issuer. The
    /// local Keycloak speaks HTTP, so the check for HTTPS metadata is switched off here and nowhere else.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithTokenIssuer(
        this IResourceBuilder<ProjectResource> service,
        IResourceBuilder<ContainerResource> keycloak,
        string issuer) => service
        .WithEnvironment("Authentication__Schemes__Bearer__Authority", issuer)
        .WithEnvironment("Authentication__Schemes__Bearer__ValidAudiences__0", "tender-scout-api")
        .WithEnvironment("Authentication__Schemes__Bearer__RequireHttpsMetadata", "false")
        .WaitFor(keycloak);
}
