using HappyMix.Application.Live;
using MediatR;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Every two minutes, asks Steam what sharing members with a verified Steam account are playing (needs <c>Steam:ApiKey</c>).</summary>
[DisallowConcurrentExecution]
internal sealed class SyncSteamPlayingJob(ISender sender) : IJob
{
    public Task Execute(IJobExecutionContext context) => sender.Send(new SyncSteamPlayingCommand(), context.CancellationToken);
}
