using HappyMix.Application.Stats;
using MediatR;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Every 10 minutes, refreshes the oldest stale Dota 2 / CS2 cards of people who share their stats.</summary>
[DisallowConcurrentExecution]
internal sealed class RefreshGameStatsJob(ISender sender) : IJob
{
    public Task Execute(IJobExecutionContext context) => sender.Send(new RefreshGameStatsCommand(), context.CancellationToken);
}
