using TenderScout.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("tenderscout");
builder.AddRabbitMQClient("messaging");

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
