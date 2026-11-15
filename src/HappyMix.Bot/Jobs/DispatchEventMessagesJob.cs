using HappyMix.Application.Events;
using MediatR;
using Microsoft.Extensions.Logging;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>
/// Sends queued event messages once a minute, at most 20 per run, so a large group or a long series never floods
/// Discord. Failed sends retry with back-off; closed DMs are recorded and the invitation stays on the dashboard.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class DispatchEventMessagesJob(ISender sender, ILogger<DispatchEventMessagesJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var result = await sender.Send(new DispatchEventMessagesCommand(), context.CancellationToken);
        if (result.IsSuccess && result.Value > 0)
            LogSent(result.Value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Count} event messages.")]
    private partial void LogSent(int count);
}
