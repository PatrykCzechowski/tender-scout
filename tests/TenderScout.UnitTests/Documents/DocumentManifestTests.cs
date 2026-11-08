using System.Text;
using TenderScout.Infrastructure.Documents;

namespace TenderScout.UnitTests.Documents;

public sealed class DocumentManifestTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Header = "doc_id,publication_number,ocid,doc_kind,path,sha256,notes";

    [Fact]
    public void Read_ValidManifest_ReturnsRows()
    {
        var rows = Read($"{Header}\nswz-001,100001-2026,ocds-1,swz,100001-2026/SWZ.pdf,{Hash},\nodp-001,100002-2026,ocds-2,odpowiedzi,100002-2026/odp.pdf,{Hash},\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new ManifestRow(1, "swz-001", "100001-2026", "swz", "100001-2026/SWZ.pdf", Hash), rows[0]);
        Assert.Equal("odpowiedzi", rows[1].Kind);
    }

    [Fact]
    public void Read_CrlfAndQuotedCommaAndBom_KeepsFieldsInPlace()
    {
        var csv = $"\uFEFF{Header}\r\nswz-001,100001-2026,ocds-1,swz,\"a/SWZ cz 1.pdf\",{Hash},\"uwaga, z przecinkiem\"\r\n";

        var row = Assert.Single(Read(csv));

        Assert.Equal("swz-001", row.DocId);
        Assert.Equal("a/SWZ cz 1.pdf", row.Path);
        Assert.Equal(Hash, row.Sha256);
    }

    [Fact]
    public void Read_WithoutKindColumn_TreatsRowsAsMainDocuments()
    {
        var row = Assert.Single(Read($"doc_id,publication_number,path,sha256\nswz-001,100001-2026,a.pdf,{Hash}\n"));

        Assert.Equal("swz", row.Kind);
    }

    [Theory]
    [InlineData("doc_id")]
    [InlineData("publication_number")]
    [InlineData("path")]
    [InlineData("sha256")]
    public void Read_MissingRequiredColumn_NamesIt(string column)
    {
        var columns = string.Join(',', new[] { "doc_id", "publication_number", "path", "sha256" }.Where(c => c != column));

        var exception = Assert.Throws<DocumentImportException>(() => Read($"{columns}\na,b,c\n"));

        Assert.Contains($"'{column}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_EmptyManifest_IsRejected()
    {
        Assert.Throws<DocumentImportException>(() => Read(string.Empty));
    }

    [Fact]
    public void Read_UnbalancedQuote_IsRejected()
    {
        Assert.Throws<DocumentImportException>(() => Read($"doc_id,publication_number,path,sha256\nswz-001,\"100001,a.pdf,{Hash}\n"));
    }

    [Theory]
    [InlineData("", "100001-2026", "a.pdf", Hash, "doc_id")]
    [InlineData("swz-001", " ", "a.pdf", Hash, "publication_number")]
    [InlineData("swz-001", "100001-2026", "", Hash, "path")]
    [InlineData("swz-001", "100001-2026", "a.pdf", "", "sha256")]
    public void Validate_BlankRequiredValue_NamesTheColumn(string docId, string number, string path, string sha256, string column)
    {
        var row = new ManifestRow(3, docId, number, "swz", path, sha256);

        var exception = Assert.Throws<DocumentImportException>(row.Validate);

        Assert.Contains($"'{column}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(docId.Length > 0 ? docId : "row 3", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("z123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Validate_MalformedHash_NamesTheDocument(string sha256)
    {
        var row = new ManifestRow(1, "swz-001", "100001-2026", "swz", "a.pdf", sha256);

        var exception = Assert.Throws<DocumentImportException>(row.Validate);

        Assert.Contains("swz-001", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ManifestRow> Read(string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return DocumentManifest.Read(stream);
    }
}
