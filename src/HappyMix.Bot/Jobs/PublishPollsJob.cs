using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Polls;
using HappyMix.Bot.Services;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>
/// Catches up on vote messages: posts votes that weren't posted when they were created (for example while Discord was
/// unavailable) and shows the result on votes whose time ran out.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class PublishPollsJob(DiscordSocketClient discord, IPollStore polls, PollMessages messages, IClock clock,
    ILogger<PublishPollsJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (discord.ConnectionState != ConnectionState.Connected) return;
        foreach (var poll in await polls.ListDiscordWorkAsync(clock.UtcNow))
        {
            try
            {
                await messages.SyncAsync(poll, context.CancellationToken);
            }
            catch (Exception error) when (!context.CancellationToken.IsCancellationRequested)
            {
                LogFailure(logger, poll.Id, poll.GuildId, error);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vote message {PollId} in server {GuildId} couldn't be synchronized")]
    private static partial void LogFailure(ILogger logger, Guid pollId, ulong guildId, Exception error);
}
