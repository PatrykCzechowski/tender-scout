using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.AddBearerAuthentication(Scopes.NoticesRead, Scopes.DocumentsAsk);
builder.Services.AddChunkEmbeddings();
builder.Services.AddScoped<NoticeQueries>();

// Streamable HTTP without sessions, the SDK's default. The tools themselves are defined in TenderTools.
// The authorization filters make the server respect the scope each tool names: a caller sees and can call
// only the tools of the scopes its token has.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .AddAuthorizationFilters()
    .WithTools<TenderTools>(TenderTools.SerializerOptions);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();

// Any request to /mcp needs a valid token with at least one of the two scopes.
app.MapMcp("/mcp").RequireAuthorization(policy => policy.RequireAssertion(
    context => context.User.HasScope(Scopes.NoticesRead) || context.User.HasScope(Scopes.DocumentsAsk)));

app.Run();
