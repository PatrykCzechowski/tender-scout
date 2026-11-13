using ModelContextProtocol;
using TenderScout.Infrastructure.Tools;

namespace TenderScout.UnitTests.Tools;

/// <summary>
/// Input rules of the tools. The tools get no database and no embedding model here, so a call that passed
/// validation would fail with a different exception: invalid input never reaches either.
/// </summary>
public sealed class TenderToolsValidationTests
{
    private const string Question = "Jaka jest wysokość wadium?";

    /// <summary>One character outside the Basic Multilingual Plane: two .NET characters.</summary>
    private const string Astral = "\U0001F600";

    private readonly TenderTools _tools = new(null!, null!, null!, null!);

    [Theory]
    [InlineData("")]
    [InlineData("  \t ")]
    public async Task SearchNotices_BlankQuery_IsRejected(string query) =>
        await AssertInvalidAsync("query", () => _tools.SearchNoticesAsync(query));

    [Fact]
    public async Task SearchNotices_QueryLongerThan200DotNetCharacters_IsRejected()
    {
        await AssertInvalidAsync("query", () => _tools.SearchNoticesAsync(new string('a', 201)));
        await AssertInvalidAsync("query", () => _tools.SearchNoticesAsync(string.Concat(Enumerable.Repeat(Astral, 101))));
        await AssertValidAsync(() => _tools.SearchNoticesAsync($"  {new string('a', 200)}  "));
        await AssertValidAsync(() => _tools.SearchNoticesAsync(string.Concat(Enumerable.Repeat(Astral, 100))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("7")]
    [InlineData("720000001")]
    [InlineData("72A")]
    [InlineData("72000000-5")]
    [InlineData("٧٢")]
    public async Task SearchNotices_CpvPrefixThatIsNot2To8AsciiDigits_IsRejected(string cpvPrefix) =>
        await AssertInvalidAsync("cpvPrefix", () => _tools.SearchNoticesAsync(cpvPrefix: cpvPrefix));

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("01/12/2026")]
    [InlineData("2026-12-01T00:00:00Z")]
    [InlineData("2026-12-01+01:00")]
    [InlineData("2026-2-01")]
    [InlineData("٢٠٢٦-12-01")]
    [InlineData(" ")]
    public async Task SearchNotices_DeadlineThatIsNotACalendarDate_IsRejected(string date)
    {
        await AssertInvalidAsync("deadlineFrom", () => _tools.SearchNoticesAsync(deadlineFrom: date));
        await AssertInvalidAsync("deadlineTo", () => _tools.SearchNoticesAsync(deadlineTo: date));
    }

    [Fact]
    public async Task SearchNotices_DeadlineToBeforeDeadlineFrom_IsRejected()
    {
        await AssertInvalidAsync("deadlineTo", () => _tools.SearchNoticesAsync(deadlineFrom: "2026-12-10", deadlineTo: "2026-12-09"));
        await AssertValidAsync(() => _tools.SearchNoticesAsync(deadlineFrom: "2026-12-10", deadlineTo: "2026-12-10"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(26)]
    public async Task SearchNotices_LimitOutside1To25_IsRejected(int limit)
    {
        await AssertInvalidAsync("limit", () => _tools.SearchNoticesAsync(limit: limit));
        await AssertValidAsync(() => _tools.SearchNoticesAsync(limit: 25));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1234567-2026")]
    [InlineData("123456/2026")]
    [InlineData("123-26")]
    [InlineData("-2026")]
    [InlineData("١-2026")]
    public async Task MalformedPublicationNumber_IsRejectedByBothToolsThatTakeOne(string number)
    {
        await AssertInvalidAsync("publicationNumber", () => _tools.GetNoticeAsync(number));
        await AssertInvalidAsync("publicationNumber", () => _tools.SearchTenderDocumentsAsync(Question, number));
    }

    [Fact]
    public async Task SearchTenderDocuments_QueryMustHave1To1000DotNetCharacters()
    {
        await AssertInvalidAsync("query", () => _tools.SearchTenderDocumentsAsync("  "));
        await AssertInvalidAsync("query", () => _tools.SearchTenderDocumentsAsync(new string('a', 1001)));
        await AssertInvalidAsync("query", () => _tools.SearchTenderDocumentsAsync(string.Concat(Enumerable.Repeat(Astral, 501))));
        await AssertValidAsync(() => _tools.SearchTenderDocumentsAsync(string.Concat(Enumerable.Repeat(Astral, 500))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(11)]
    public async Task SearchTenderDocuments_LimitOutside1To10_IsRejected(int limit) =>
        await AssertInvalidAsync("limit", () => _tools.SearchTenderDocumentsAsync(Question, limit: limit));

    /// <summary>The message is what a model reads to correct its call, so it must name the parameter.</summary>
    private static async Task AssertInvalidAsync(string parameter, Func<Task> call) =>
        Assert.StartsWith(parameter, (await Assert.ThrowsAsync<McpException>(call)).Message, StringComparison.Ordinal);

    /// <summary>Valid input gets past validation and only then fails, on the missing database or model.</summary>
    private static async Task AssertValidAsync(Func<Task> call) =>
        Assert.IsNotType<McpException>(await Assert.ThrowsAnyAsync<Exception>(call));
}
