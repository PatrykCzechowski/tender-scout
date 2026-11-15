using TenderScout.Infrastructure.Persistence;
using TenderScout.MigrationService;

// With --demo-data the same image loads the invented demo data instead; the option is not passed on as configuration.
var demoData = args.Contains(DemoDataWorker.Argument);
var builder = Host.CreateApplicationBuilder(args.Except([DemoDataWorker.Argument]).ToArray());

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(MigrationWorker.ActivitySourceName));
if (demoData)
{
    builder.Services.AddHostedService<DemoDataWorker>();
}
else
{
    builder.Services.AddHostedService<MigrationWorker>();
}

var host = builder.Build();
host.Run();
