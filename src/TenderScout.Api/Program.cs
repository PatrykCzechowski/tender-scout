var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDataSource("tenderscout");
builder.AddRabbitMQClient("messaging");

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
