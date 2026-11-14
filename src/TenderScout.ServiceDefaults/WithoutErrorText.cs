using System.Diagnostics;
using OpenTelemetry;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Keeps a failed span's error status but drops its status text before export. Microsoft.Extensions.AI and MassTransit
/// put an exception's message there, and a message may quote a file path or the data being processed. The type of the
/// error stays in the span's <c>error.type</c> or exception event.
/// </summary>
internal sealed class WithoutErrorText : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        if (data.Status == ActivityStatusCode.Error)
        {
            data.SetStatus(ActivityStatusCode.Error);
        }
    }
}
