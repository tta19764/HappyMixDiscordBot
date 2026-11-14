using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Application.Guilds;

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
