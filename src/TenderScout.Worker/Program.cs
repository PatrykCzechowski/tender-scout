using MassTransit;
using MassTransit.Logging;
using MassTransit.Monitoring;
using TenderScout.Infrastructure.Ingestion;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Ted;
using TenderScout.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout");

builder.Services.AddNoticePersistence();
builder.Services.AddTedClient();
builder.Services.AddNoticeIngestion();

var rabbitMq = builder.Configuration.GetConnectionString("messaging")
    ?? throw new InvalidOperationException("Connection string 'messaging' (RabbitMQ) is not configured.");

builder.Services.AddMassTransit(bus => bus
    .AddNoticeIngestionConsumers()
    .UsingTenderScoutRabbitMq(new Uri(rabbitMq)));

// Start the bus before the poller publishes its first message.
builder.Services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(DiagnosticHeaders.DefaultListenerName))
    .WithMetrics(metrics => metrics.AddMeter(InstrumentationOptions.MeterName));

builder.Services.AddHostedService<NoticePollingService>();

var host = builder.Build();
host.Run();
