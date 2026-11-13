using Scalar.AspNetCore;
using TenderScout.Api;
using TenderScout.Infrastructure.Answering;
using TenderScout.Infrastructure.Embeddings;
using TenderScout.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<TenderScoutDbContext>("tenderscout", configureDbContextOptions: options => options.UsePgvector());

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddChunkEmbeddings();
builder.Services.AddQuestionAnswering();
builder.Services.AddScoped<NoticeQueries>();

var app = builder.Build();

app.UseExceptionHandler();

// The web page: index.html and its files from wwwroot, served at "/". Unknown paths stay 404.
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

app.MapGet("/notices", NoticesEndpoint.HandleAsync)
    .WithSummary("Lists stored notices, soonest submission deadline first.")
    .WithDescription(
        "The filters are those of the search_notices tool, plus hasDocuments. limit is 50 when not given and at most " +
        "200; total counts every matching notice, whatever the limit. Notices without a deadline come last.")
    .Produces<NoticeList>()
    .ProducesValidationProblem();

app.Run();
