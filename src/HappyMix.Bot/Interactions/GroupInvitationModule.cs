using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Guilds;
using MediatR;

namespace HappyMix.Bot.Interactions;

/// <summary>Handles group invitations from Discord direct messages.</summary>
public sealed class GroupInvitationModule(ISender sender)
    : InteractionModuleBase<SocketInteractionContext>
{
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
