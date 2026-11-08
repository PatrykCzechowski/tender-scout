using TenderScout.DocumentEmbedding;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddChunkEmbeddings();
builder.Services.AddHostedService<DocumentEmbeddingWorker>();

var host = builder.Build();
host.Run();
