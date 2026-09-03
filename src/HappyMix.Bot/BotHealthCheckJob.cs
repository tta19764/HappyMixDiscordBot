using HappyMix.Application.Status;
using Microsoft.Extensions.Logging;
using Quartz;

namespace HappyMix.Bot;

[DisallowConcurrentExecution]
public sealed partial class BotHealthCheckJob(
    GetBotStatusHandler getBotStatus,
    ILogger<BotHealthCheckJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var status = getBotStatus.Handle();
        LogHealthCheck(logger, status.CheckedAtUtc);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Scheduled bot health check completed at {CheckedAtUtc}")]
    private static partial void LogHealthCheck(ILogger logger, DateTimeOffset checkedAtUtc);
}
