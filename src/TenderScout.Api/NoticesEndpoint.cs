using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using TenderScout.Infrastructure.Persistence;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.Api;

/// <summary>
/// <c>GET /notices</c>: stored notices, soonest submission deadline first, with the filters of the
/// <c>search_notices</c> tool and a documents flag. Every parameter is read as text, so each invalid one is reported
/// as problem details in every environment, all of them together.
/// </summary>
public static class NoticesEndpoint
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    public static async Task<IResult> HandleAsync(
        [Description("Words that must all occur in the title or the buyer name, 1 to 200 characters.")] string? query,
        [Description("The first 2 to 8 digits of a CPV code, compared with the main and the other CPV codes.")] string? cpvPrefix,
        [Description("Earliest submission deadline, yyyy-MM-dd.")] string? deadlineFrom,
        [Description("Latest submission deadline, yyyy-MM-dd, not before deadlineFrom.")] string? deadlineTo,
        [Description("true: only notices with a stored document; false: only notices without one.")] string? hasDocuments,
        [Description("Maximum number of notices, 1 to 200; 50 when not given.")] string? limit,
        NoticeQueries notices,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        // The rules of the tool, so both accept and reject the same values; they report with McpException.
        var text = Apply(errors, nameof(query), () => TenderTools.NoticeQuery(query));
        var prefix = Apply(errors, nameof(cpvPrefix), () => TenderTools.CpvPrefix(cpvPrefix));
        var from = Apply(errors, nameof(deadlineFrom), () => TenderTools.Date(deadlineFrom, nameof(deadlineFrom)));
        var to = Apply(errors, nameof(deadlineTo), () => TenderTools.Date(deadlineTo, nameof(deadlineTo)));
        Apply(errors, nameof(deadlineTo), () => { TenderTools.CheckDeadlineRange(from, to); return to; });

        bool? documents = hasDocuments switch
        {
            null => null,
            _ when hasDocuments.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            _ when hasDocuments.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => AddError(errors, nameof(hasDocuments), "hasDocuments must be true or false."),
        };

        var count = DefaultLimit;
        if (limit is not null && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count is < 1 or > MaxLimit))
        {
            AddError(errors, nameof(limit), $"limit must be a whole number from 1 to {MaxLimit}.");
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return Results.Ok(await notices.ListAsync(text, prefix, from, to, documents, count, cancellationToken).ConfigureAwait(false));
    }

    private static T? Apply<T>(Dictionary<string, string[]> errors, string parameter, Func<T?> rule)
    {
        try
        {
            return rule();
        }
        catch (McpException exception)
        {
            errors[parameter] = [exception.Message];
            return default;
        }
    }

    private static bool? AddError(Dictionary<string, string[]> errors, string parameter, string message)
    {
        errors[parameter] = [message];
        return null;
    }
}
