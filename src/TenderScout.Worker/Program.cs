using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Ted;
using TenderScout.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout");
builder.AddRabbitMQClient("messaging");

builder.Services.AddNoticePersistence();
builder.Services.AddTedClient();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
