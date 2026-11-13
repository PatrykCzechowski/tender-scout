using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddChunkEmbeddings();
builder.Services.AddScoped<NoticeQueries>();

// Streamable HTTP without sessions, the SDK's default. The tools themselves are defined in TenderTools.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<TenderTools>(TenderTools.SerializerOptions);

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapMcp("/mcp");

app.Run();
