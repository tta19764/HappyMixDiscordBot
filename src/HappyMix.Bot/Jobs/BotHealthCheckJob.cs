using HappyMix.Application.Status.GetBotStatus;
using MediatR;
using Microsoft.Extensions.Logging;
using Quartz;

namespace HappyMix.Bot.Jobs;

[DisallowConcurrentExecution]
public sealed partial class BotHealthCheckJob(
    ISender sender,
    ILogger<BotHealthCheckJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var result = await sender.Send(new GetBotStatusQuery(), context.CancellationToken);

        if (result.IsFailure)
        {
            throw new JobExecutionException(result.Error.Description);
        }

        LogHealthCheck(logger, result.Value.CheckedAtUtc);
    }

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Scheduled bot health check completed at {CheckedAtUtc}")]
    private static partial void LogHealthCheck(ILogger logger, DateTimeOffset checkedAtUtc);
}
