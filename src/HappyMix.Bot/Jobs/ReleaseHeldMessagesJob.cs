using HappyMix.Application.Quiet;
using MediatR;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Every minute, sends personal messages whose recipient's do-not-disturb has ended and that still apply.</summary>
[DisallowConcurrentExecution]
internal sealed class ReleaseHeldMessagesJob(ISender sender) : IJob
{
    public Task Execute(IJobExecutionContext context) => sender.Send(new ReleaseHeldMessagesCommand(), context.CancellationToken);
}
