using Discord;
using Discord.WebSocket;
using HappyMix.Application.Stats;
using MediatR;
using Microsoft.Extensions.Options;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Hourly: posts the Dota 2 lane meta where a server's schedule says it's due.</summary>
[DisallowConcurrentExecution]
internal sealed class RunDotaMetaScheduleJob(ISender sender, DiscordSocketClient discord,
    IOptions<HappyMix.Infrastructure.PandaScore.PandaScoreOptions> options) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return;
        await sender.Send(new RunDotaMetaScheduleCommand(discord.Guilds.Select(guild => guild.Id).ToArray(), options.Value.TimeZoneId),
            context.CancellationToken);
    }
}
