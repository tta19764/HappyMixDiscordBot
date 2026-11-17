using System.Globalization;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Events;
using MediatR;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Persistence;
using Quartz;

namespace HappyMix.Bot.Jobs;

[DisallowConcurrentExecution]
public sealed partial class PublishEventChannelsJob(DiscordSocketClient discord, IEventStore events,
    NotificationRouteStore routes, ISender sender, IConfiguration configuration,
    ILogger<PublishEventChannelsJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (discord.ConnectionState != ConnectionState.Connected) return;
        var now = DateTimeOffset.UtcNow;
        int minutes = Math.Clamp(configuration.GetValue("EventNotifications:ReminderMinutes", 15), 1, 1440);
        foreach (var guild in discord.Guilds)
        {
            if (!guild.IsConnected) continue;
            try
            {
                foreach (var details in await events.ListConfirmedAsync(guild.Id, now.AddMinutes(-5), now.AddMinutes(minutes)))
                {
                    if (details.Event.CanceledAt is not null) continue;
                    var route = await routes.GetAsync(guild.Id, "event", details.Event.Id);
                    if (route is null && details.Event.GroupId is { } groupId) route = await routes.GetAsync(guild.Id, "group", groupId);
                    if (route is not { Enabled: true } || !ulong.TryParse(route.ChannelId, CultureInfo.InvariantCulture, out var channelId) ||
                        guild.GetTextChannel(channelId) is not { } channel) continue;
                    foreach (var occurrence in details.Occurrences.Where(item => item.Status == OccurrenceStatus.Confirmed &&
                        item.Start >= now.AddMinutes(-5) && item.Start <= now.AddMinutes(minutes)))
                    {
                        var attendees = details.Responses.Where(answer => answer.Date == occurrence.Date && answer.Accepted)
                            .Select(answer => answer.UserId).Append(details.Event.OrganizerId).Distinct().Order().ToArray();
                        foreach (var (batch, index) in attendees.Chunk(50).Select((batch, index) => (batch, index)))
                        {
                            string key = FormattableString.Invariant($"event-reminder/{guild.Id}/{details.Event.Id:N}/{occurrence.Date:yyyy-MM-dd}/{index}");
                            if (await routes.WasSentAsync(key)) continue;
                            var recent = await channel.GetMessagesAsync(100).FlattenAsync();
                            if (recent.Any(message => message.Author.Id == discord.CurrentUser.Id && message.Embeds.Any(embed => embed.Footer?.Text == key)))
                            { await routes.MarkSentAsync(key); continue; }
                            var inVoice = (await sender.Send(new VoiceParticipantsQuery(guild.Id,
                                guild.Users.Where(user => user.VoiceChannel is not null).Select(user => user.Id).ToArray()), context.CancellationToken)).Value.ToHashSet();
                            var absent = new List<ulong>();
                            foreach (ulong memberId in batch.Where(id => !inVoice.Contains(id)))
                                if (await ((IGuild)guild).GetUserAsync(memberId) is not null) absent.Add(memberId);
                            string body = NotificationText.Get(route.Language, "Notify_EventReminder", Format.Sanitize(details.Event.Title),
                                $"<t:{occurrence.Start.ToUnixTimeSeconds()}:F>");
                            var embed = new EmbedBuilder().WithDescription(body).WithColor(Color.Blue).WithFooter(key).Build();
                            await channel.SendMessageAsync(absent.Count == 0 ? null : string.Join(" ", absent.Select(id => $"<@{id}>")),
                                embed: embed, allowedMentions: new AllowedMentions { UserIds = absent },
                                options: new RequestOptions { CancelToken = context.CancellationToken });
                            await routes.MarkSentAsync(key);
                        }
                    }
                }
            }
            catch (Exception error) when (!context.CancellationToken.IsCancellationRequested)
            { LogFailure(logger, guild.Id, error); }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event channel reminders failed for server {GuildId}")]
    private static partial void LogFailure(ILogger logger, ulong guildId, Exception error);
}
