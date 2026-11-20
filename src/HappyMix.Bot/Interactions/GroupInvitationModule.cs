using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Guilds;
using MediatR;

namespace HappyMix.Bot.Interactions;

/// <summary>Handles group invitations from Discord direct messages.</summary>
public sealed class GroupInvitationModule(ISender sender, Microsoft.Extensions.Options.IOptions<HappyMix.Application.Abstractions.Guilds.CommunityDefaults> community)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Let in / Decline on the organizer's "asked to join" message.</summary>
    [ComponentInteraction("group-request:*:*:*:*", ignoreGroupNames: true)]
    public Task ResolveRequestAsync(string server, string group, string member, string approve) =>
        ulong.TryParse(server, out var guildId) && Guid.TryParseExact(group, "N", out var groupId) && ulong.TryParse(member, out var memberId)
            ? AnswerAsync(new ResolveJoinRequestCommand(new GroupActor(guildId, Context.User.Id, false), groupId, memberId, approve == "1"),
                approve == "1" ? "Groups_Msg_Approved" : "Groups_Msg_Declined")
            : RespondAsync("Invalid request.", ephemeral: true);

    /// <summary>Add / Dismiss on the organizer's "suggested a game" message.</summary>
    [ComponentInteraction("group-suggestion:*:*:*:*", ignoreGroupNames: true)]
    public Task ResolveSuggestionAsync(string server, string group, string suggestion, string accept) =>
        ulong.TryParse(server, out var guildId) && Guid.TryParseExact(group, "N", out var groupId) && Guid.TryParseExact(suggestion, "N", out var suggestionId)
            ? AnswerAsync(new ResolveGameSuggestionCommand(new GroupActor(guildId, Context.User.Id, false), groupId, suggestionId, accept == "1"),
                accept == "1" ? "Game_added" : "Groups_Msg_Dismissed")
            : RespondAsync("Invalid suggestion.", ephemeral: true);

    /// <summary>Runs the same command as the dashboard and replaces the buttons with the outcome, in the community language.</summary>
    private async Task AnswerAsync(GroupCommand command, string successKey)
    {
        if (Context.Interaction is not SocketMessageComponent component)
            return;
        await component.DeferAsync();
        var result = await sender.Send(command);
        string language = community.Value.LanguageCode;
        string outcome = result.IsSuccess ? HappyMix.Bot.Services.NotificationText.Get(language, successKey)
            : HappyMix.Bot.Services.NotificationText.Get(language, HappyMix.Bot.Web.DashboardLocalization.KeyFor(result.Error.Description));
        await component.ModifyOriginalResponseAsync(message =>
        {
            message.Content = component.Message.Content + "\n**" + (result.IsSuccess ? "✓ " : "") + outcome + "**";
            message.Components = new ComponentBuilder().Build();
            message.AllowedMentions = AllowedMentions.None;
        });
    }

    [ComponentInteraction("group-invite:*:*:*", ignoreGroupNames: true)]
    public async Task RespondToInviteAsync(string action, string server, string invitation)
    {
        if (Context.Interaction is not SocketMessageComponent component || Context.Guild is not null ||
            !ulong.TryParse(server, out var guildId) || !Guid.TryParseExact(invitation, "N", out var token) ||
            action is not ("accept" or "decline"))
        {
            await RespondAsync("Invalid group invitation.", ephemeral: true);
            return;
        }

        await component.DeferAsync();
        bool accept = action == "accept";
        var result = await sender.Send(new ResolveGroupInviteCommand(
            new GroupActor(guildId, Context.User.Id, false), null, token, accept));
        await component.ModifyOriginalResponseAsync(message =>
        {
            message.Content = result.IsFailure ? result.Error.Description
                : accept ? $"You joined **{result.Value.Name}**." : $"You declined the invitation to **{result.Value.Name}**.";
            if (result.IsSuccess && result.Value.RoleSyncFailed)
                message.Content += " A server moderator can restore your group role with /group repair-roles.";
            message.Components = new ComponentBuilder().Build();
            message.AllowedMentions = AllowedMentions.None;
        });
    }
}
