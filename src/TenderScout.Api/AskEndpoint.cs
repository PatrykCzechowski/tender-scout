using System.Text.RegularExpressions;
using TenderScout.Infrastructure.Answering;

namespace TenderScout.Api;

/// <param name="Question">Up to <see cref="AskEndpoint.MaxQuestionLength"/> characters.</param>
/// <param name="PublicationNumber">Limits the search to one tender, e.g. <c>123456-2026</c>.</param>
public sealed record AskRequest(string? Question, string? PublicationNumber);

/// <summary><c>POST /ask</c>: 200 with an answer or a refusal, 400 for bad input, 503 when a model cannot be reached.</summary>
public static partial class AskEndpoint
{
    internal const int MaxQuestionLength = 1000;

    public static async Task<IResult> HandleAsync(
        AskRequest request,
        QuestionAnswerer answerer,
        ILogger<QuestionAnswerer> logger,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        try
        {
            return Results.Ok(await answerer.AskAsync(request.Question!, request.PublicationNumber, cancellationToken));
        }
        catch (Exception exception) when (QuestionAnswerer.IsUnavailable(exception, cancellationToken))
        {
            LogUnavailable(logger, exception);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The language model is unavailable.");
        }
    }

    private static Dictionary<string, string[]> Validate(AskRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaxQuestionLength)
        {
            errors["question"] = [$"The question is required and may have at most {MaxQuestionLength} characters."];
        }

        if (request.PublicationNumber is not null && !PublicationNumberPattern().IsMatch(request.PublicationNumber))
        {
            errors["publicationNumber"] = ["The publication number must look like 123456-2026."];
        }

        return errors;
    }

    [GeneratedRegex(@"^\d{1,6}-\d{4}$")]
    private static partial Regex PublicationNumberPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A model could not be reached; answering 503")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}
