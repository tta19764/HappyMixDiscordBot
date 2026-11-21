using Discord;
using Discord.WebSocket;
using HappyMix.Application.Stats;
using MediatR;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Every 10 minutes, reads sharing members' recent matches and posts new games played together.</summary>
[DisallowConcurrentExecution]
internal sealed class RefreshMatchesJob(ISender sender, DiscordSocketClient discord) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return;
        await sender.Send(new RefreshMatchesCommand(discord.Guilds.Select(guild => guild.Id).ToArray()), context.CancellationToken);
    }
}
