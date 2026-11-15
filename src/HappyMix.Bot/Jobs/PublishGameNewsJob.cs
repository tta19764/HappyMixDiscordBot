using HappyMix.Bot.Services;
using Quartz;

namespace HappyMix.Bot.Jobs;

[DisallowConcurrentExecution]
public sealed class PublishGameNewsJob(GameNewsService news) : IJob
{
    public Task Execute(IJobExecutionContext context) => news.PublishAsync(context.CancellationToken);
}
