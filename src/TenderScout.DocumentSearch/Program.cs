using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

// The words are not passed to the host, which would read them as configuration.
var query = string.Join(' ', args);

var builder = Host.CreateApplicationBuilder();

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddChunkEmbeddings();

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();

return await scope.ServiceProvider.GetRequiredService<DocumentSearchCommand>().RunAsync(query, Console.Out, Console.Error);
