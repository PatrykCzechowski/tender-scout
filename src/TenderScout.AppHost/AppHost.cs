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

// Applies EF Core migrations and exits; the services below start only after it succeeds.
var migrations = builder.AddProject<Projects.TenderScout_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database);

builder.AddProject<Projects.TenderScout_Api>("api")
    .WithReference(database)
    .WithReference(rabbitmq)
    .WaitFor(database)
    .WaitFor(rabbitmq)
    .WaitForCompletion(migrations)
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
