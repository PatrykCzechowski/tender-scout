using TenderScout.Infrastructure.Ted;

namespace TenderScout.UnitTests.Ted;

public sealed class TedNoticeMapperTests
{
    [Fact]
    public void Map_MultipleLots_UsesEarliestDeadlineAndSkipsInvalidOnes()
    {
        var notice = TedNoticeMapper.Map(Dto() with
        {
            TenderDeadlines = ["2026-12-01+01:00", "not-a-date", "2026-11-15+01:00"],
        });

        Assert.Equal(new DateOnly(2026, 11, 15), notice.SubmissionDeadline);
    }

    [Fact]
    public void Map_NoPolishTitle_FallsBackToEnglishThenAnyLanguage()
    {
        Assert.Equal("English", TedNoticeMapper.Map(Dto() with { Title = new() { ["deu"] = "Deutsch", ["eng"] = "English" } }).Title);
        Assert.Equal("Deutsch", TedNoticeMapper.Map(Dto() with { Title = new() { ["deu"] = "Deutsch" } }).Title);
    }

    [Fact]
    public void Map_MissingPublicationNumber_Throws()
    {
        Assert.Throws<TedApiException>(() => TedNoticeMapper.Map(Dto() with { PublicationNumber = null }));
    }

    [Fact]
    public void Map_InvalidPublicationDate_Throws()
    {
        var exception = Assert.Throws<TedApiException>(() => TedNoticeMapper.Map(Dto() with { PublicationDate = "20/10/2026" }));

        Assert.Contains("722731-2026", exception.Message, StringComparison.Ordinal);
    }

    private static TedNoticeDto Dto() =>
        new("722731-2026", null, null, null, null, "2026-10-20+02:00", null, null);
}
