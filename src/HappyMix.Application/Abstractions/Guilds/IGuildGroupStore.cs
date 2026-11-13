namespace HappyMix.Application.Abstractions.Guilds;

public sealed record GuildGroup(string Name, ulong OrganizerId, ulong[] Members, ulong[] Invites, Dictionary<ulong, Guid>? InviteTokens = null);

public interface IGuildGroupStore
{
    Task<IReadOnlyList<GuildGroup>> ListAsync(ulong guildId);
    Task UpdateAsync(ulong guildId, string name, Func<GuildGroup?, GuildGroup?> update);
}
