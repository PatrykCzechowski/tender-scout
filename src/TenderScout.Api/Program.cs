using TenderScout.Api;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddProblemDetails();
builder.Services.AddChunkEmbeddings();
builder.Services.AddQuestionAnswering();

var app = builder.Build();

app.UseExceptionHandler();

app.MapDefaultEndpoints();
app.MapPost("/ask", AskEndpoint.HandleAsync);

app.Run();
