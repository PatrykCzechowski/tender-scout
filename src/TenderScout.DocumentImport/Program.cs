using TenderScout.DocumentImport;
using TenderScout.Infrastructure.Documents;
using TenderScout.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout");

builder.Services.AddDocumentImport();
builder.Services.AddHostedService<DocumentImportWorker>();

var host = builder.Build();
host.Run();
