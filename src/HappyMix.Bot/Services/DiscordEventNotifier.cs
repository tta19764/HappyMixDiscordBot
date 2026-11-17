using System.Globalization;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Events;
using Microsoft.Extensions.Configuration;

namespace HappyMix.Bot.Services;

/// <summary>
/// Event messages by Discord DM. Invitations carry Accept/Decline buttons for their date; confirmations and
/// cancellations are plain updates. Times use Discord timestamps, which every reader sees in their own time zone.
/// </summary>
internal sealed class DiscordEventNotifier(DiscordSocketClient client, IConfiguration configuration) : IEventNotifier
{
    public async Task<EventDeliveryOutcome> SendAsync(EventOutboxItem item, CancellationToken cancellationToken)
    {
        if (client.ConnectionState != ConnectionState.Connected)
            return EventDeliveryOutcome.RetryLater;

        var guildEvent = item.Event;
        string server = client.GetGuild(guildEvent.GuildId)?.Name ?? "your server";
        string title = Format.Sanitize(guildEvent.Title);
        string when = $"<t:{item.Occurrence.Start.ToUnixTimeSeconds()}:F> – <t:{item.Occurrence.End.ToUnixTimeSeconds()}:t> ({guildEvent.TimeZone})";
        string? link = DashboardLink(guildEvent.GuildId, guildEvent.Id, item.Occurrence.Date);
        string footer = link is null ? "" : $"\n[Open in the dashboard]({link})";
        string organizer = $"<@{guildEvent.OrganizerId}>";

        string text;
        MessageComponent? buttons = null;
        switch (item.Kind)
        {
            case EventNotificationKind.Invite:
                bool repeats = guildEvent.Recurrence != EventRecurrence.Once;
                string repeat = repeats ? $"\n🔁 {Schedule(guildEvent)}. One answer covers every date; you can skip single dates in the dashboard." : "";
                text = $"{organizer} invited you to **{title}** in **{Format.Sanitize(server)}**.\n{(repeats ? "First date: " : "")}{when}{repeat}{Description(guildEvent)}{footer}";
                string date = item.Occurrence.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                // A repeating event gets one invitation whose buttons answer every date.
                buttons = new ComponentBuilder()
                    .WithButton(repeats ? "Accept all dates" : "Accept", $"event:{(repeats ? "acceptall" : "accept")}:{guildEvent.GuildId}:{guildEvent.Id:N}:{date}", ButtonStyle.Success)
                    .WithButton(repeats ? "Decline all dates" : "Decline", $"event:{(repeats ? "declineall" : "decline")}:{guildEvent.GuildId}:{guildEvent.Id:N}:{date}", ButtonStyle.Secondary)
                    .Build();
                break;
            case EventNotificationKind.Confirmed:
                text = $"✅ **{title}** is confirmed for {when}. See you there!{footer}";
                break;
            default:
                text = guildEvent.CanceledAt is not null
                    ? $"❌ {organizer} canceled every remaining date of **{title}** in **{Format.Sanitize(server)}**.{footer}"
                    : $"❌ **{title}** on {when} was canceled by {organizer}.{footer}";
                break;
        }

        try
        {
            var user = await client.Rest.GetUserAsync(item.UserId);
            if (user is null)
                return EventDeliveryOutcome.Undeliverable;
            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync(text, components: buttons, allowedMentions: AllowedMentions.None,
                options: new RequestOptions { Timeout = 10000, CancelToken = cancellationToken });
            return EventDeliveryOutcome.Sent;
        }
        catch (HttpException exception) when (exception.DiscordCode == DiscordErrorCode.CannotSendMessageToUser ||
            exception.HttpCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            return EventDeliveryOutcome.Undeliverable;
        }
        catch (Exception exception) when (exception is HttpException or TimeoutException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return EventDeliveryOutcome.RetryLater;
        }
    }

    private static string Schedule(GuildEvent guildEvent)
    {
        string days = guildEvent.Recurrence == EventRecurrence.Daily ? "Every day"
            : "Every " + string.Join(", ", guildEvent.Weekdays.OrderBy(day => ((int)day + 6) % 7).Select(day => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(day)));
        return guildEvent.Until is { } until ? $"{days} until {until.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}" : days;
    }

    private static string Description(GuildEvent guildEvent) =>
        guildEvent.Description is { Length: > 0 } text ? $"\n> {Format.Sanitize(text.Length > 300 ? text[..300] + "…" : text).Replace("\n", "\n> ", StringComparison.Ordinal)}" : "";

    private string? DashboardLink(ulong guildId, Guid eventId, DateOnly date) =>
        configuration.GetValue<bool>("Dashboard:Enabled") && Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) &&
        origin.Scheme == Uri.UriSchemeHttps
            ? new Uri(origin, $"/events/{guildId}/{eventId}?date={date:yyyy-MM-dd}").ToString()
            : null;
}
