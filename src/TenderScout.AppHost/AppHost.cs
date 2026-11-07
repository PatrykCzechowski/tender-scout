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

builder.AddProject<Projects.TenderScout_Api>("api")
    .WithReference(database)
    .WithReference(rabbitmq)
    .WaitFor(database)
    .WaitFor(rabbitmq)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TenderScout_Worker>("worker")
    .WithReference(database)
    .WithReference(rabbitmq)
    .WaitFor(database)
    .WaitFor(rabbitmq);

builder.Build().Run();
