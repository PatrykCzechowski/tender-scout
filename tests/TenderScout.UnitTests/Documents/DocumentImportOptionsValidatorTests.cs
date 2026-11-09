using TenderScout.Infrastructure.Documents;

namespace TenderScout.UnitTests.Documents;

public sealed class DocumentImportOptionsValidatorTests
{
    [Fact]
    public void Validate_CompleteOptions_Succeeds()
    {
        var result = new DocumentImportOptionsValidator().Validate(null, CreateOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankDocumentsFolder_NamesTheSetting(string folder)
    {
        var options = CreateOptions();
        options.DocumentsFolder = folder;

        var result = new DocumentImportOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("DocumentImport:DocumentsFolder", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_BlankManifestPath_NamesTheSetting()
    {
        var options = CreateOptions();
        options.ManifestPath = " ";

        var result = new DocumentImportOptionsValidator().Validate(null, options);

        Assert.Contains("DocumentImport:ManifestPath", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NoDocumentKinds_NamesTheSetting()
    {
        var options = CreateOptions();
        options.DocKinds = [];

        var result = new DocumentImportOptionsValidator().Validate(null, options);

        Assert.Contains("DocumentImport:DocKinds", result.FailureMessage, StringComparison.Ordinal);
    }

    private static DocumentImportOptions CreateOptions() =>
        new()
        {
            DocumentsFolder = "documents",
            ManifestPath = "manifest.csv",
            DocKinds = ["swz"],
        };
}
