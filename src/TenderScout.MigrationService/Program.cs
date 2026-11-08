using TenderScout.Infrastructure.Persistence;
using TenderScout.MigrationService;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(MigrationWorker.ActivitySourceName));
builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
