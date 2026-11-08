using MassTransit;
using Microsoft.Extensions.Logging;
using TenderScout.Contracts.Notices;
using TenderScout.Domain.Notices;

namespace TenderScout.Infrastructure.Ingestion;

/// <summary>
/// Stores each discovered notice. Safe to run any number of times for the same message:
/// the upsert is keyed by publication number and leaves identical notices untouched.
/// </summary>
public sealed partial class NoticeDiscoveredConsumer(INoticeRepository repository, ILogger<NoticeDiscoveredConsumer> logger)
    : IConsumer<NoticeDiscovered>
{
    public async Task Consume(ConsumeContext<NoticeDiscovered> context)
    {
        var result = await repository.UpsertAsync(context.Message.ToNotice(), context.CancellationToken).ConfigureAwait(false);
        LogStored(logger, context.Message.PublicationNumber, result);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notice {PublicationNumber}: {Result}")]
    private static partial void LogStored(ILogger logger, string publicationNumber, NoticeUpsertResult result);
}
