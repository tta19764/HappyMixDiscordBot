using HappyMix.Application.Matches.SynchronizeMatches;
using MediatR;
using Microsoft.Extensions.Logging;
using Quartz;

namespace HappyMix.Bot.Jobs;

[DisallowConcurrentExecution]
public sealed partial class SynchronizeMatchesJob(
    ISender sender,
    ILogger<SynchronizeMatchesJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var result = await sender.Send(new SynchronizeMatchesCommand(), context.CancellationToken);

        if (result.IsFailure)
        {
            throw new JobExecutionException(result.Error.Description);
        }

        LogSynchronizationCompleted(logger);
    }

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "PandaScore match synchronization completed")]
    private static partial void LogSynchronizationCompleted(ILogger logger);
}
