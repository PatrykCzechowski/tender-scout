using TenderScout.Infrastructure.Answering;

namespace TenderScout.UnitTests.Answering;

/// <summary>Which text in a question names a tender: the table of the forms, row by row.</summary>
public sealed class TenderNumbersTests
{
    [Theory]
    [InlineData("Jakie wadium w 100101-2026?", "100101-2026")]
    [InlineData("Wadium (ogłoszenie 100101-2026)?", "100101-2026")]
    [InlineData("Termin w 100101-2026.", "100101-2026")]
    [InlineData("Ogłoszenie nr 100101-2026, jakie wadium?", "100101-2026")]
    [InlineData("Wadium w 5-2026?", "5-2026")]
    [InlineData("Wadium w 12-2026?", "12-2026")]
    [InlineData("Wadium w 100101-2026, a termin w 100101-2026?", "100101-2026")]
    [InlineData("Wadium w 100101-2026 i w 100202-2026?", "100101-2026|100202-2026")]
    [InlineData("Wadium w 100202-2026 i w 100101-2026?", "100202-2026|100101-2026")]
    [InlineData("Oferty do 15-12-2026?", "")]
    [InlineData("Oferty do 2026-12-15?", "")]
    [InlineData("Oferty do 15.12.2026?", "")]
    [InlineData("Sprawa ZP.271.5-2026?", "")]
    [InlineData("Sprawa 271/5-2026?", "")]
    [InlineData("Ogłoszenie 1234567-2026?", "")]
    [InlineData("Ogłoszenie 100101-20261?", "")]
    [InlineData("Ogłoszenie 100101-2026r?", "")]
    [InlineData("Ogłoszenie SWZ100101-2026?", "")]
    [InlineData("Telefon 601-4567?", "")]
    [InlineData("Ogłoszenie 123456-0000?", "")]
    [InlineData("Ogłoszenie 100101 - 2026?", "")]
    [InlineData("Ogłoszenie 100101–2026?", "")]
    [InlineData("Ogłoszenie 100101/2026?", "")]
    [InlineData("Ogłoszenie 100101?", "")]
    [InlineData("Wadium 40 000 zł i kod CPV 72000000-5?", "")]
    public void Question_NamesTheTendersOfItsRow(string question, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), QuestionAnswerer.TenderNumbersIn(question));
    }
}
