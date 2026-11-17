using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Events;
using MediatR;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Persistence;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>
/// Posts group and event channel notifications: a reminder before each confirmed date (lead time per route), and,
/// for routes with updates switched on, a note when an event is scheduled, a date is confirmed, or a date or the
/// whole series is canceled. Every post is recorded so it is sent once, even across restarts.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class PublishEventChannelsJob(DiscordSocketClient discord, IEventStore events,
    NotificationRouteStore routes, ISender sender, EventNotificationSettings settings, IConfiguration configuration,
    ILogger<PublishEventChannelsJob> logger) : IJob
{
    /// <summary>Changes older than this are not announced, so switching updates on doesn't post old news.</summary>
    internal static readonly TimeSpan UpdateWindow = TimeSpan.FromHours(6);

    public async Task Execute(IJobExecutionContext context)
    {
        if (discord.ConnectionState != ConnectionState.Connected) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var guild in discord.Guilds)
        {
            if (!guild.IsConnected) continue;
            try
            {
                await RemindAsync(guild, now, context.CancellationToken);
                await PostUpdatesAsync(guild, now, context.CancellationToken);
            }
            catch (Exception error) when (!context.CancellationToken.IsCancellationRequested)
            { LogFailure(logger, guild.Id, error); }
        }
    }

    private async Task RemindAsync(SocketGuild guild, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var upcoming = await events.ListConfirmedAsync(guild.Id, now.AddMinutes(-5), now.AddMinutes(EventNotificationSettings.MaxReminderMinutes));
        foreach (var details in upcoming)
        {
            if (details.Event.CanceledAt is not null) continue;
            if (await GetRouteAsync(guild, details.Event) is not { } target) continue;
            var (route, channel) = target;
            int lead = route.ReminderMinutes ?? settings.DefaultReminderMinutes;
            foreach (var occurrence in details.Occurrences.Where(item => item.Status == OccurrenceStatus.Confirmed &&
                item.Start >= now.AddMinutes(-5) && item.Start <= now.AddMinutes(lead)))
            {
                var attendees = Attendees(details, occurrence.Date);
                foreach (var (batch, index) in attendees.Chunk(50).Select((batch, index) => (batch, index)))
                {
                    string key = FormattableString.Invariant($"event-reminder/{guild.Id}/{details.Event.Id:N}/{occurrence.Date:yyyy-MM-dd}/{index}");
                    if (await AlreadySentAsync(channel, key)) continue;
                    var inVoice = (await sender.Send(new VoiceParticipantsQuery(guild.Id,
                        guild.Users.Where(user => user.VoiceChannel is not null).Select(user => user.Id).ToArray()), cancellationToken)).Value.ToHashSet();
                    var absent = new List<ulong>();
                    foreach (ulong memberId in batch.Where(id => !inVoice.Contains(id)))
                        if (await ((IGuild)guild).GetUserAsync(memberId) is not null) absent.Add(memberId);
                    var embed = BuildReminder(route.Language, details.Event, occurrence, attendees.Length, key, Link(details.Event, occurrence.Date));
                    await channel.SendMessageAsync(absent.Count == 0 ? null : string.Join(" ", absent.Select(id => $"<@{id}>")),
                        embed: embed, allowedMentions: new AllowedMentions { UserIds = absent },
                        options: new RequestOptions { CancelToken = cancellationToken });
                    await routes.MarkSentAsync(key);
                }
            }
        }
    }

    private async Task PostUpdatesAsync(SocketGuild guild, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - UpdateWindow;
        foreach (var details in await events.ListChangedAsync(guild.Id, since))
        {
            if (await GetRouteAsync(guild, details.Event) is not ({ Updates: true } route, var channel)) continue;
            var guildEvent = details.Event;
            string id = FormattableString.Invariant($"{guild.Id}/{guildEvent.Id:N}");
            var posts = new List<(string Key, EmbedBuilder Embed)>();
            var future = details.Occurrences.Where(item => item.Start > now).ToArray();
            if (guildEvent.CreatedAt >= since && guildEvent.CanceledAt is null && future.FirstOrDefault() is { } first)
                posts.Add(($"event-scheduled/{id}", BuildUpdate(route.Language, "scheduled", guildEvent, first, details.Invitees.Count + 1, Link(guildEvent, first.Date))));
            if (guildEvent.CanceledAt >= since)
                posts.Add(($"event-canceled/{id}/all", BuildUpdate(route.Language, "series-canceled", guildEvent, null, 0, null)));
            else
            {
                foreach (var occurrence in future)
                {
                    string date = occurrence.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (occurrence.Status == OccurrenceStatus.Confirmed && occurrence.ConfirmedAt >= since)
                        posts.Add(($"event-confirmed/{id}/{date}", BuildUpdate(route.Language, "confirmed", guildEvent, occurrence,
                            Attendees(details, occurrence.Date).Length, Link(guildEvent, occurrence.Date))));
                    else if (occurrence.Status == OccurrenceStatus.Canceled && occurrence.CanceledAt >= since)
                        posts.Add(($"event-canceled/{id}/{date}", BuildUpdate(route.Language, "canceled", guildEvent, occurrence, 0, null)));
                }
            }
            foreach (var (key, embed) in posts)
            {
                if (await AlreadySentAsync(channel, key)) continue;
                await channel.SendMessageAsync(embed: embed.WithFooter(Footer(route.Language, "Notify_EventUpdateFooter", key)).Build(),
                    allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
                await routes.MarkSentAsync(key);
            }
        }
    }

    /// <summary>The event's own route, else its group's, when switched on and pointing at a channel the bot can use.</summary>
    private async Task<(NotificationRoute Route, ITextChannel Channel)?> GetRouteAsync(SocketGuild guild, GuildEvent guildEvent)
    {
        var route = await routes.GetAsync(guild.Id, "event", guildEvent.Id);
        if (route is null && guildEvent.GroupId is { } groupId) route = await routes.GetAsync(guild.Id, "group", groupId);
        return route is { Enabled: true } && guild.GetTextChannel(route.Channel()) is { } channel ? (route, channel) : null;
    }

    /// <summary>Recorded in the database; a matching footer in the channel also counts, in case the record was lost after posting.</summary>
    private async Task<bool> AlreadySentAsync(ITextChannel channel, string key)
    {
        if (await routes.WasSentAsync(key)) return true;
        string reference = Reference(key);
        var recent = await channel.GetMessagesAsync(100).FlattenAsync();
        if (!recent.Any(message => message.Author.Id == discord.CurrentUser.Id &&
            message.Embeds.Any(embed => embed.Footer?.Text is { } text && (text == key || text.EndsWith(reference, StringComparison.Ordinal)))))
            return false;
        await routes.MarkSentAsync(key);
        return true;
    }

    private static ulong[] Attendees(EventDetails details, DateOnly date) =>
        details.Responses.Where(answer => answer.Date == date && answer.Accepted)
            .Select(answer => answer.UserId).Append(details.Event.OrganizerId).Distinct().Order().ToArray();

    internal static Embed BuildReminder(string language, GuildEvent guildEvent, EventOccurrence occurrence, int going, string key, string? link)
    {
        long start = occurrence.Start.ToUnixTimeSeconds();
        var embed = new EmbedBuilder()
            .WithTitle("⏰ " + Trim(NotificationText.Get(language, "Notify_EventReminderTitle", guildEvent.Title), 250))
            .WithDescription(NotificationText.Get(language, "Notify_EventStarts", $"<t:{start}:R>", $"<t:{start}:F>", $"<t:{occurrence.End.ToUnixTimeSeconds()}:t>")
                + "\n" + NotificationText.Get(language, "Notify_EventVoiceHint"))
            .AddField(NotificationText.Get(language, "Notify_EventGoing"), going.ToString(CultureInfo.InvariantCulture), inline: true)
            .WithColor(Color.Orange)
            .WithFooter(Footer(language, "Notify_EventFooter", key));
        if (link is not null) embed.WithUrl(link);
        return embed.Build();
    }

    internal static EmbedBuilder BuildUpdate(string language, string kind, GuildEvent guildEvent, EventOccurrence? occurrence, int going, string? link)
    {
        string when = occurrence is null ? ""
            : $"<t:{occurrence.Start.ToUnixTimeSeconds()}:F> – <t:{occurrence.End.ToUnixTimeSeconds()}:t>";
        var (icon, titleKey, bodyKey, color) = kind switch
        {
            "scheduled" => ("📅", "Notify_EventScheduledTitle", guildEvent.Recurrence == EventRecurrence.Once ? "Notify_EventScheduled" : "Notify_EventScheduledSeries", Color.Blue),
            "confirmed" => ("✅", "Notify_EventConfirmedTitle", "Notify_EventConfirmed", Color.Green),
            "canceled" => ("❌", "Notify_EventCanceledTitle", "Notify_EventCanceled", Color.Red),
            _ => ("❌", "Notify_EventCanceledTitle", "Notify_EventSeriesCanceled", Color.Red)
        };
        var embed = new EmbedBuilder()
            .WithTitle(icon + " " + Trim(NotificationText.Get(language, titleKey, guildEvent.Title), 250))
            .WithDescription(NotificationText.Get(language, bodyKey, when, $"<@{guildEvent.OrganizerId}>"))
            .WithColor(color);
        if (going > 0) embed.AddField(NotificationText.Get(language, kind == "scheduled" ? "Notify_EventInvited" : "Notify_EventGoing"),
            going.ToString(CultureInfo.InvariantCulture), inline: true);
        if (link is not null) embed.WithUrl(link);
        return embed;
    }

    private static EmbedFooterBuilder Footer(string language, string labelKey, string key) =>
        new EmbedFooterBuilder().WithText($"{NotificationText.Get(language, labelKey)} · {Reference(key)}");

    /// <summary>A short, stable reference for a delivery key, shown in the footer instead of the raw key.</summary>
    internal static string Reference(string key) =>
        "ref " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10];

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    private string? Link(GuildEvent guildEvent, DateOnly date) =>
        configuration.GetValue<bool>("Dashboard:Enabled") && Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) &&
        origin.Scheme == Uri.UriSchemeHttps
            ? new Uri(origin, $"/events/{guildEvent.GuildId}/{guildEvent.Id}?date={date:yyyy-MM-dd}").ToString()
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event channel notifications failed for server {GuildId}")]
    private static partial void LogFailure(ILogger logger, ulong guildId, Exception error);
}
