using System.Diagnostics;

namespace TenderScout.UnitTests.Evaluation;

/// <summary>
/// The evaluation command as a process, with a hosted chat provider. Every address points where nothing listens, and
/// the key is invented, so no run can reach a model, a database or the network.
/// </summary>
public sealed class HostedGoldenRunTests : IDisposable
{
    private const string Refusal = "A golden set is answered and judged by local models only";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("tender-scout-hosted-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", null, true)]
    [InlineData("", "sk-or-invented-test-key", true)]
    [InlineData("--retrieval-only", "sk-or-invented-test-key", false)]
    [InlineData("--agent-cases", "sk-or-invented-test-key", false)]
    [InlineData("--write-fixture", "sk-or-invented-test-key", false)]
    public async Task GoldenRunWithHostedChat_IsRefusedBeforeAnythingIsRead_AndTheOtherRunsAreNot(string option, string? key, bool refused)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _folder.FullName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TenderScout.Evaluation.dll"));
        if (option.Length > 0)
        {
            start.ArgumentList.Add(option);
        }

        foreach (var name in new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "OPENROUTER_API_KEY", "OTEL_EXPORTER_OTLP_ENDPOINT" })
        {
            start.Environment.Remove(name);
        }

        start.Environment["Chat__Provider"] = "OpenRouter";
        start.Environment["Chat__Endpoint"] = "https://127.0.0.1:9/api/v1";
        start.Environment["Embedding__Endpoint"] = "http://127.0.0.1:9";
        start.Environment["ConnectionStrings__tenderscout"] = "Host=127.0.0.1;Port=9;Database=none;Timeout=1";
        start.Environment["Evaluation__GoldenSetPath"] = Path.Combine(_folder.FullName, "golden.jsonl");
        if (key is not null)
        {
            start.Environment["OPENROUTER_API_KEY"] = key;
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(Ct);
        await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);

        Assert.Equal(refused, (await errors).Contains(Refusal, StringComparison.Ordinal));
        Assert.NotEqual(0, process.ExitCode);
        if (refused)
        {
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(_folder.EnumerateFileSystemInfos());
        }
    }

    public void Dispose() => _folder.Delete(recursive: true);
}
