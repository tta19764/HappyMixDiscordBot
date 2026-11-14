using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Application.Guilds;

internal static class GroupInvitation
{
    /// <summary>
    /// Resolves an invitation addressed to <paramref name="member"/>. Accepting adds the shared member
    /// once; every family invitation and token is cleared either way.
    /// </summary>
    internal static GuildGroup? Resolve(GuildGroup group, IReadOnlyCollection<ulong> family, ulong member, bool accept)
    {
        if (!group.Invites.Contains(member))
            return null;

        bool alreadyMember = group.Members.Any(family.Contains);
        return group with
        {
            Members = accept && !alreadyMember ? group.Members.Append(member).ToArray() : group.Members,
            Invites = group.Invites.Where(id => !family.Contains(id)).ToArray(),
            InviteTokens = RemoveTokens(group, family) is { Count: > 0 } left ? left : null
        };
    }

    /// <summary>A copy of the tokens without the listed accounts, or null when there were none.</summary>
    internal static Dictionary<ulong, Guid>? RemoveTokens(GuildGroup group, IReadOnlyCollection<ulong> accounts)
    {
        if (group.InviteTokens is not { } saved)
            return null;

        var tokens = new Dictionary<ulong, Guid>(saved);
        foreach (ulong account in accounts)
            tokens.Remove(account);
        return tokens;
    }
}
