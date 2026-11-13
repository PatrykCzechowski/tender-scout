using TenderScout.Evaluation;

namespace TenderScout.UnitTests.Evaluation;

public sealed class AgentCaseSetTests : IDisposable
{
    private const string Valid = """{"id": "ok-1", "question": "Pytanie?", "expectedCalls": [], "allowedExtraCalls": []}""";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("tender-scout-agent-cases-");

    [Fact]
    public void Case_MayExpectNoCallAndAllowNone_AndUsesTheThreeWaysToWriteAnArgument()
    {
        var set = Load(
            Valid,
            """{"id": "ok-2", "question": "Pytanie?", "expectedCalls": [{"tool": "search_notices", "arguments": {"cpvPrefix": ["72", "72000000"], "query": {"words": ["serwer"]}, "limit": 5}}], "allowedExtraCalls": ["get_notice"]}""");

        Assert.Equal(["ok-1", "ok-2"], set.Cases.Select(c => c.Id));
        Assert.Empty(set.Cases[0].ExpectedCalls);
        Assert.Equal(["cpvPrefix", "query", "limit"], set.Cases[1].ExpectedCalls[0].Arguments.Keys);
        Assert.Equal(new DateOnly(2026, 12, 7), set.ReferenceDate);
        Assert.Equal("100101-2026", Assert.Single(set.Notices).PublicationNumber);
    }

    [Theory]
    [InlineData("""{"id": "ok-1", "question": "Inne?", "expectedCalls": [], "allowedExtraCalls": []}""", "ok-1")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": [{"tool": "find_tender", "arguments": {}}], "allowedExtraCalls": []}""", "bad")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": [], "allowedExtraCalls": ["find_tender"]}""", "bad")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": [{"tool": "search_notices", "arguments": {"buyer": "Gmina"}}], "allowedExtraCalls": []}""", "bad")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": [{"tool": "search_notices", "arguments": {"publicationNumber": "100101-2026"}}], "allowedExtraCalls": []}""", "bad")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": [{"tool": "search_notices", "arguments": {"query": {"contains": "serwer"}}}], "allowedExtraCalls": []}""", "bad")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": [{"tool": "search_notices", "arguments": {"limit": [true]}}], "allowedExtraCalls": []}""", "bad")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": [""", "line 2")]
    [InlineData("""{"question": "Pytanie?", "expectedCalls": [], "allowedExtraCalls": []}""", "line 2")]
    [InlineData("""{"id": "bad", "expectedCalls": [], "allowedExtraCalls": []}""", "line 2")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "allowedExtraCalls": []}""", "line 2")]
    [InlineData("""{"id": "bad", "question": "Pytanie?", "expectedCalls": []}""", "line 2")]
    public void BrokenCase_StopsTheLoad_AndIsNamed(string secondLine, string named)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Load(Valid, secondLine));

        Assert.Contains(named, exception.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _folder.Delete(recursive: true);

    private AgentCaseSet Load(params string[] cases)
    {
        File.WriteAllLines(Path.Combine(_folder.FullName, AgentCaseSet.CasesFile), cases);
        File.WriteAllText(
            Path.Combine(_folder.FullName, AgentCaseSet.NoticesFile),
            """{"publicationNumber": "100101-2026", "title": "Dostawa", "buyerName": null, "mainCpvCode": null, "cpvCodes": [], "publicationDate": "2026-12-01", "submissionDeadline": null, "link": null}""");
        File.WriteAllText(Path.Combine(_folder.FullName, AgentCaseSet.ReferenceDateFile), """{"referenceDate": "2026-12-07"}""");
        return AgentCaseSet.Load(_folder.FullName);
    }
}
