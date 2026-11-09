using HappyMix.Application.Matches.SynchronizeMatches;
using MediatR;
using Microsoft.Extensions.Logging;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Polls the followed main-team CS2 and Dota 2 feeds without overlapping executions.</summary>
/// <param name="sender">Dispatches the synchronization use case.</param>
/// <param name="logger">Records successful synchronization.</param>
[DisallowConcurrentExecution]
public sealed partial class SynchronizeMatchesJob(
    ISender sender,
    ILogger<SynchronizeMatchesJob> logger) : IJob
{
    /// <inheritdoc />
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
