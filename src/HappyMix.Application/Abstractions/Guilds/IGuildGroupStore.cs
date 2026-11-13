namespace HappyMix.Application.Abstractions.Guilds;

public sealed record GuildGroup(string Name, ulong OrganizerId, ulong[] Members, ulong[] Invites);

public interface IGuildGroupStore
{
    Task<IReadOnlyList<GuildGroup>> ListAsync(ulong guildId);
    Task UpdateAsync(ulong guildId, string name, Func<GuildGroup?, GuildGroup?> update);
}
