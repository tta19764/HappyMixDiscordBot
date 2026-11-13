using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Bot.Interactions;

/// <summary>Handles group invitations from Discord direct messages.</summary>
public sealed class GroupInvitationModule(IGuildGroupStore groups) : InteractionModuleBase<SocketInteractionContext>
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
        ulong userId = Context.User.Id;
        bool accept = action == "accept";
        var group = (await groups.ListAsync(guildId)).FirstOrDefault(item =>
            item.InviteTokens?.GetValueOrDefault(userId) == token);
        bool resolved = false;
        if (group is not null)
        {
            await groups.UpdateAsync(guildId, group.Name, current =>
            {
                if (current?.InviteTokens?.GetValueOrDefault(userId) != token)
                    return current;

                var updated = GroupInvitation.Resolve(current, userId, accept);
                resolved = updated is not null;
                return updated ?? current;
            });
        }

        await component.ModifyOriginalResponseAsync(message =>
        {
            message.Content = !resolved ? "This invitation is no longer pending."
                : accept ? $"You joined **{group!.Name}**." : $"You declined the invitation to **{group!.Name}**.";
            message.Components = new ComponentBuilder().Build();
            message.AllowedMentions = AllowedMentions.None;
        });
    }
}

internal static class GroupInvitation
{
    internal static GuildGroup? Resolve(GuildGroup group, ulong userId, bool accept)
    {
        if (!group.Invites.Contains(userId))
            return null;

        return group with
        {
            Members = accept ? group.Members.Append(userId).Distinct().ToArray() : group.Members,
            Invites = group.Invites.Where(id => id != userId).ToArray(),
            InviteTokens = RemoveToken(group, userId)
        };
    }

    internal static Dictionary<ulong, Guid>? RemoveToken(GuildGroup group, ulong userId)
    {
        if (group.InviteTokens is not { } saved)
            return null;

        var tokens = new Dictionary<ulong, Guid>(saved);
        tokens.Remove(userId);
        return tokens.Count == 0 ? null : tokens;
    }
}
