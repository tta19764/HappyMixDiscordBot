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
            action is not ("accept" or "decline") || !ulong.TryParse(server, out var guildId) ||
            !Guid.TryParseExact(eventText, "N", out var eventId) ||
            !DateOnly.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            await RespondAsync("Invalid event invitation.", ephemeral: true);
            return;
        }

        await component.DeferAsync();
        bool accept = action == "accept";
        var result = await sender.Send(new RespondToEventCommand(guildId, Context.User.Id, eventId, date, accept));
        string original = component.Message.Content;
        string status = result.IsFailure ? $"⚠️ {result.Error.Description}"
            : result.Value.Accepted
                ? result.Value.Status == OccurrenceStatus.Confirmed ? "✅ You're going. This date is confirmed." : "✅ You accepted. The organizer hasn't confirmed this date yet."
                : result.Value.Withdrawn ? "↩️ You withdrew. The organizer can see that you can't make it." : "You declined this date.";

        // Keep the buttons so the answer can be changed (for example, withdrawing later); a canceled or past date drops them.
        bool keepButtons = result.IsSuccess;
        await component.ModifyOriginalResponseAsync(message =>
        {
            message.Content = $"{StripStatus(original)}\n\n**{status}**";
            if (!keepButtons)
                message.Components = new ComponentBuilder().Build();
            message.AllowedMentions = AllowedMentions.None;
        });
    }

    private static string StripStatus(string content)
    {
        int index = content.LastIndexOf("\n\n**", StringComparison.Ordinal);
        return index > 0 ? content[..index] : content;
    }
}
