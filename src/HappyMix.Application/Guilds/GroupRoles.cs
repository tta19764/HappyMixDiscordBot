using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Application.Guilds;

public interface IGroupRoleGateway
{
    Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId);
    Task<ulong> CreateAsync(ulong guildId, string name);
    Task DeleteAsync(ulong guildId, ulong roleId);
    Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta);
}

public sealed record GroupRoleDelta(IReadOnlyList<ulong> Add, IReadOnlyList<ulong> Remove);
public sealed class GroupRoleException(string message, Exception inner) : Exception(message, inner);

internal sealed class GroupRoles(IGuildGroupStore groups, IGroupRoleGateway gateway) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);

    public void Dispose() => gate.Dispose();

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    public Task DeleteAsync(ulong guildId, GuildGroup group) =>
        group.RoleId is { } id ? gateway.DeleteAsync(guildId, id) : Task.CompletedTask;

    public async Task SyncAsync(ulong guildId, ulong? userId = null, string? groupName = null)
    {
        var saved = await groups.ListAsync(guildId);
        if (saved.Count == 0)
            return;

        var existing = await gateway.GetRoleIdsAsync(guildId);
        var roles = new List<(ulong Id, HashSet<ulong> Members)>();
        string? groupKey = groupName is null ? null : GuildGroup.GetNameKey(groupName);
        foreach (var group in saved)
        {
            if (groupKey is not null && GuildGroup.GetNameKey(group.Name) != groupKey)
                continue;

            ulong? id = group.RoleId;
            if (id is null || !existing.Contains(id.Value))
            {
                ulong created = await gateway.CreateAsync(guildId, group.Name);
                try
                {
                    await groups.UpdateAsync(guildId, group.Name, current => current! with { RoleId = created });
                }
                catch
                {
                    await gateway.DeleteAsync(guildId, created);
                    throw;
                }
                id = created;
            }
            roles.Add((id.Value, group.Members.ToHashSet()));
        }

        await gateway.ApplyAsync(guildId, userId, (member, assigned) =>
        {
            var add = new List<ulong>();
            var remove = new List<ulong>();
            foreach (var (id, members) in roles)
            {
                bool shouldHaveRole = members.Contains(member);
                bool hasRole = assigned.Contains(id);
                if (shouldHaveRole && !hasRole)
                    add.Add(id);
                if (!shouldHaveRole && hasRole)
                    remove.Add(id);
            }
            return new GroupRoleDelta(add, remove);
        });
    }
}
