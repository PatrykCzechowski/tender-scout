using TenderScout.Evaluation;

namespace TenderScout.UnitTests.Evaluation;

public sealed class GoldenRecordTests
{
    private static readonly GoldenRecord Record = new(
        "q-1", "Ile wynosi wadium?", Answerable: true, Paraphrase: false, "doc-10", 5, "Wadium w wysokości 50 000,00 zł należy wnieść", "auto");

    [Fact]
    public void Quote_IsFoundIgnoringCaseAndLineBreaks()
    {
        Assert.True(Record.IsAnsweredBy("doc-10", "3.1. wadium   w wysokości\n50 000,00 zł należy wnieść przed upływem terminu."));
    }

    [Theory]
    [InlineData("doc-11", "Wadium w wysokości 50 000,00 zł należy wnieść")]
    [InlineData("doc-10", "Wadium w wysokości 25 000,00 zł należy wnieść")]
    public void OtherDocumentOrOtherText_DoesNotAnswer(string docId, string text)
    {
        Assert.False(Record.IsAnsweredBy(docId, text));
    }

    [Fact]
    public void UnanswerableRecord_IsAnsweredByNothing()
    {
        var record = new GoldenRecord("q-2", "Jaka jest pogoda?", Answerable: false, Paraphrase: false, null, null, null, "auto");

        Assert.False(record.IsAnsweredBy("doc-10", "Jaka jest pogoda?"));
    }

    [Fact]
    public void Load_ReadsSnakeCaseLines_IgnoringUnknownFieldsAndBlankLines()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path,
        [
            """{"id": "rag-001", "question": "Kiedy?", "topic": "deadline", "answerable": true, "paraphrase": true, "doc_id": "swz-001", "publication_number": "100001-2026", "page": 14, "quote": "Termin", "expected_answer": "1.12", "label_source": "verified"}""",
            string.Empty,
            """{"id": "rag-033", "question": "Kto wygrał?", "topic": "none", "answerable": false, "paraphrase": false, "doc_id": null, "page": null, "quote": null, "label_source": "auto"}""",
        ]);

        var records = GoldenRecord.Load(path);
        File.Delete(path);

        Assert.Equal(
            [
                new GoldenRecord("rag-001", "Kiedy?", true, true, "swz-001", 14, "Termin", "verified", "100001-2026"),
                new GoldenRecord("rag-033", "Kto wygrał?", false, false, null, null, null, "auto"),
            ],
            records);
    }
}
