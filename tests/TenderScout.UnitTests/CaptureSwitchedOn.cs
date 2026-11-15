using System.Runtime.CompilerServices;

namespace TenderScout.UnitTests;

/// <summary>
/// Microsoft.Extensions.AI records the texts of messages when this standard variable is "true", and reads it once per
/// process. It is set before any test runs, so the telemetry tests show that the application's clients switch the
/// recording off themselves instead of relying on the default.
/// </summary>
internal static class CaptureSwitchedOn
{
#pragma warning disable CA2255 // A test assembly is the place where the process-wide default has to be set first.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void SwitchOn() => Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");
}
