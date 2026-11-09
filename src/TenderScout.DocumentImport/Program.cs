using MassTransit;
using TenderScout.DocumentImport;
using TenderScout.Infrastructure.Documents;
using TenderScout.Infrastructure.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddDocumentImport();

var rabbitMq = builder.Configuration.GetConnectionString("messaging")
    ?? throw new InvalidOperationException("Connection string 'messaging' (RabbitMQ) is not configured.");

builder.Services.AddMassTransit(bus => bus.UsingTenderScoutRabbitMq(new Uri(rabbitMq)));

// Start the bus before the import publishes its first message.
builder.Services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);

builder.Services.AddHostedService<DocumentImportWorker>();

var host = builder.Build();
host.Run();
