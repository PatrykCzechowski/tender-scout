using System.Globalization;
using System.Text.Json;
using TenderScout.Domain.Notices;
using TenderScout.Evaluation;

namespace TenderScout.IntegrationTests.Evaluation;

/// <summary>The public cases in <c>evals/agent</c>: a broken or inconsistent case fails here, without a model.</summary>
public sealed class AgentCasesTests
{
    private static readonly AgentCaseSet Set = AgentCaseSet.Load(Path.Combine(AppContext.BaseDirectory, "Evaluation", "agent"));

    [Fact]
    public void TwelveCases_HaveTheDecidedSpread()
    {
        string[] Shape(AgentCase @case) => [.. @case.ExpectedCalls.Select(call => call.Tool)];
        var shapes = Set.Cases.Select(Shape).ToList();

        Assert.Equal(12, Set.Cases.Count);
        Assert.Equal(3, shapes.Count(tools => tools.Length == 1 && tools[0] != "search_tender_documents"));
        Assert.Equal(3, shapes.Count(tools => tools.SequenceEqual(["search_tender_documents"])));
        Assert.Equal(4, shapes.Count(tools => tools.SequenceEqual(["search_notices", "search_tender_documents"])));
        Assert.Equal(2, shapes.Count(tools => tools.Length == 0));
        Assert.True(Set.Cases.Count(c => c.ExpectedCalls.Any(call => call.Arguments.ContainsKey("deadlineFrom"))) >= 3);
        Assert.True(Set.Cases.Count(c => c.ExpectedCalls.Any(call => call.Arguments.ContainsKey("cpvPrefix"))) >= 2);
        Assert.All(Set.Cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Question)));
    }

    [Fact]
    public void ExpectedCalls_FitTheInventedNotices()
    {
        foreach (var @case in Set.Cases)
        {
            // An expected number is a notice of the set, and a notice search that comes before it finds that notice.
            var searches = @case.ExpectedCalls.Where(call => call.Tool == "search_notices").ToList();
            var numbers = @case.ExpectedCalls.Where(call => call.Arguments.ContainsKey("publicationNumber"))
                .Select(call => call.Arguments["publicationNumber"].GetString())
                .ToList();
            var targets = Set.Notices.Where(notice => numbers.Contains(notice.PublicationNumber)).ToList();

            Assert.Equal(numbers.Count, targets.Count);
            Assert.All(searches, search => Assert.Contains(Set.Notices, notice => Finds(search, notice)));
            Assert.All(searches, search => Assert.All(targets, target => Assert.True(Finds(search, target), @case.Id)));
        }
    }

    [Fact]
    public void ExpectedDeadlines_AreNotBeforeTheReferenceDate()
    {
        var dates = Set.Cases.SelectMany(c => c.ExpectedCalls)
            .SelectMany(call => call.Arguments.Where(a => a.Key.StartsWith("deadline", StringComparison.Ordinal)))
            .Select(a => DateOnly.ParseExact(a.Value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(dates);
        Assert.All(dates, date => Assert.True(date >= Set.ReferenceDate));
    }

    [Fact]
    public void EveryDocumentOfTheSyntheticSet_HasItsInventedNotice()
    {
        var tenders = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Evaluation", SyntheticSet.PassagesFile))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("publication_number").GetString())
            .Distinct()
            .ToList();

        Assert.Equal(5, tenders.Count);
        Assert.Equal(10, Set.Notices.Count);
        Assert.All(tenders, tender => Assert.Contains(Set.Notices, notice => notice.PublicationNumber == tender));
    }

    /// <summary>Whether a notice passes the filters an expected notice search names, as the tool applies them.</summary>
    private static bool Finds(ExpectedCall search, Notice notice)
    {
        string? Text(string argument) => search.Arguments.TryGetValue(argument, out var value) ? value.GetString() : null;
        var words = search.Arguments.TryGetValue("query", out var query)
            ? query.GetProperty("words").EnumerateArray().Select(word => word.GetString()!)
            : [];
        var deadline = notice.SubmissionDeadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return words.All(word => $"{notice.Title} {notice.BuyerName}".Contains(word, StringComparison.OrdinalIgnoreCase))
            && (Text("cpvPrefix") is not { } prefix || notice.CpvCodes.Any(code => code.StartsWith(prefix, StringComparison.Ordinal)))
            && (Text("deadlineFrom") is not { } from || string.CompareOrdinal(deadline, from) >= 0)
            && (Text("deadlineTo") is not { } to || (deadline is not null && string.CompareOrdinal(deadline, to) <= 0));
    }
}
