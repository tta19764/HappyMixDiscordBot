using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Events;
using MediatR;

namespace HappyMix.Bot.Interactions;

/// <summary>Accept and Decline buttons on event invitations sent by direct message.</summary>
public sealed class EventInvitationModule(ISender sender) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("event:*:*:*:*", ignoreGroupNames: true)]
    public async Task AnswerAsync(string action, string server, string eventText, string dateText)
    {
        if (Context.Interaction is not SocketMessageComponent component || Context.Guild is not null ||
            action is not ("accept" or "decline" or "acceptall" or "declineall") || !ulong.TryParse(server, out var guildId) ||
            !Guid.TryParseExact(eventText, "N", out var eventId) ||
            !DateOnly.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            await RespondAsync("Invalid event invitation.", ephemeral: true);
            return;
        }

        await component.DeferAsync();
        bool accept = action is "accept" or "acceptall";
        bool series = action.EndsWith("all", StringComparison.Ordinal);
        var result = await sender.Send(new RespondToEventCommand(guildId, Context.User.Id, eventId, date, accept, series));
        string original = component.Message?.Content ?? "";
        string status = result.IsFailure ? $"⚠️ {result.Error.Description}"
            : series
                ? result.Value.Accepted ? "✅ You joined every date. Skip a single date in the dashboard if you can't make it."
                    : result.Value.Withdrawn ? "↩️ You left every remaining date. The organizer can see that you can't make it." : "You declined every date. No more invitations for this event."
            : result.Value.Accepted
                ? result.Value.Status == OccurrenceStatus.Confirmed ? "✅ You're going. This date is confirmed." : "✅ You accepted. The organizer hasn't confirmed this date yet."
                : result.Value.Withdrawn ? "↩️ You withdrew. The organizer can see that you can't make it." : "You declined this date.";

        // After an answer only the opposite choice stays: Withdraw once accepted, Accept once declined. A failed answer
        // (canceled or past date) drops the buttons.
        var buttons = new ComponentBuilder();
        if (result.IsSuccess)
        {
            string id = $"{guildId}:{eventId:N}:{dateText}";
            if (result.Value.Accepted)
                buttons.WithButton(series ? "Leave all dates" : "Withdraw", $"event:{(series ? "declineall" : "decline")}:{id}", ButtonStyle.Danger);
            else
                buttons.WithButton(series ? "Join all dates" : "Accept", $"event:{(series ? "acceptall" : "accept")}:{id}", ButtonStyle.Success);
        }
        await component.ModifyOriginalResponseAsync(message =>
        {
            message.Content = $"{StripStatus(original)}\n\n**{status}**";
            message.Components = buttons.Build();
            message.AllowedMentions = AllowedMentions.None;
        });
    }

    private static string StripStatus(string content)
    {
        int index = content.LastIndexOf("\n\n**", StringComparison.Ordinal);
        return index > 0 ? content[..index] : content;
    }
}
