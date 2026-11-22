using Discord;
using Discord.WebSocket;
using HappyMix.Application.Stats;
using MediatR;
using Microsoft.Extensions.Options;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Hourly: on Monday from 10:00 in each server's zone, posts last week's games-together summary where it's on.</summary>
[DisallowConcurrentExecution]
internal sealed class PostWeeklyDigestsJob(ISender sender, DiscordSocketClient discord,
    IOptions<HappyMix.Infrastructure.PandaScore.PandaScoreOptions> options) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return;
        await sender.Send(new PostWeeklyDigestsCommand(discord.Guilds.Select(guild => guild.Id).ToArray(), options.Value.TimeZoneId),
            context.CancellationToken);
    }
}
