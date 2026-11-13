using Scalar.AspNetCore;
using TenderScout.Api;
using TenderScout.Infrastructure.Agent;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddProblemDetails();

// A body that is not JSON is the caller's mistake in every environment: 400, not an exception as in Development.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddOpenApi();
builder.Services.AddChunkEmbeddings();
builder.Services.AddQuestionAnswering();
builder.Services.AddTenderAgent();

var app = builder.Build();

app.UseExceptionHandler();

// Gives an error response that has no body, such as that 400, problem details.
app.UseStatusCodePages();

// The ask page: index.html and its files from wwwroot, served at "/". Unknown paths stay 404.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDefaultEndpoints();
app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

// The handler returns IResult, so the responses are declared here for the OpenAPI document.
app.MapPost("/ask", AskEndpoint.HandleAsync)
    .WithSummary("Answers a question from the stored tender documents.")
    .WithDescription("200 holds an answer with its citations, or a refusal with refused set to true and no citations.")
    .Produces<AskResponse>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/agent", AgentEndpoint.HandleAsync)
    .WithSummary("Answers a question that needs several searches, with a model that calls the notice and document tools.")
    .WithDescription("200 holds an answer with the notices and passages it rests on, or a refusal, and the tool calls that were made. limitReached is true when the search stopped at a limit.")
    .Produces<AgentResponse>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.Run();
