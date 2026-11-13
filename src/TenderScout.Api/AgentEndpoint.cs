using Microsoft.Extensions.Options;
using TenderScout.Infrastructure.Agent;
using TenderScout.Infrastructure.Answering;

namespace TenderScout.Api;

/// <param name="Question">1 to <see cref="AskEndpoint.MaxQuestionLength"/> characters.</param>
public sealed record AgentRequest(string? Question);

/// <summary>
/// <c>POST /agent</c>: 200 with an answer or a refusal and the tool calls that were made, 400 for bad input, 503 when
/// a model cannot be reached or no agent model is set.
/// </summary>
public static partial class AgentEndpoint
{
    public static async Task<IResult> HandleAsync(
        AgentRequest request,
        TenderAgent agent,
        IOptions<AgentOptions> options,
        ILogger<TenderAgent> logger,
        CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question) || question.Length > AskEndpoint.MaxQuestionLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["question"] = [$"The question is required and may have at most {AskEndpoint.MaxQuestionLength} characters."],
            });
        }

        var model = options.Value.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            LogNoModel(logger);
            return Unavailable();
        }

        try
        {
            return Results.Ok(await agent.AskAsync(question, cancellationToken));
        }
        catch (Exception exception) when (QuestionAnswerer.IsUnavailable(exception, cancellationToken))
        {
            LogUnavailable(logger, exception, model);
            return Unavailable();
        }
    }

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The language model is unavailable.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent:Model is not set; answering 503")]
    private static partial void LogNoModel(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A model could not be reached for the agent (agent model: {Model}); answering 503")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string model);
}
