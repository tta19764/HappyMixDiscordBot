using System.Globalization;
using System.Text.Json;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

internal sealed class SqliteGuildGroupStore(IDbContextFactory<HappyMixDbContext> factory) : IGuildGroupStore
{
    public async Task<IReadOnlyList<GuildGroup>> ListAsync(ulong guildId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string scope = Scope(guildId);
        var rows = await context.Set<GuildGroupRecord>().Where(row => row.GuildId == scope)
            .OrderBy(row => row.NameKey).ToListAsync();

        // Rows written before durable IDs existed get one on first read; the migration normally backfills them.
        if (rows.Any(row => row.GroupId is null))
        {
            foreach (var row in rows.Where(row => row.GroupId is null))
                row.GroupId = Guid.NewGuid();
            await context.SaveChangesAsync();
        }

        return rows.Select(ToGroup).ToArray();
    }

    public async Task UpdateAsync(ulong guildId, string name, Func<GuildGroup?, GuildGroup?> update)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        string scope = Scope(guildId);
        string key = GuildGroup.GetNameKey(name);
        var rows = context.Set<GuildGroupRecord>();
        var row = await rows.FindAsync(scope, key);
        var group = update(row is null ? null : ToGroup(row));
        if (group is null)
        {
            if (row is not null)
            {
                rows.Remove(row);
                if (row.GroupId is { } removedId)
                    context.Set<GuildGroupGameRecord>().RemoveRange(await context.Set<GuildGroupGameRecord>()
                        .Where(game => game.GuildId == scope && game.GroupId == removedId).ToListAsync());
            }
        }
        else if (row is null)
        {
            rows.Add(new GuildGroupRecord
            {
                GuildId = scope,
                NameKey = key,
                GroupId = group.Id == Guid.Empty ? Guid.NewGuid() : group.Id,
                Data = Serialize(group)
            });
        }
        else
        {
            row.GroupId ??= Guid.NewGuid();
            row.Data = Serialize(group);
        }

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<GroupGame>> ListGamesAsync(ulong guildId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string scope = Scope(guildId);
        var rows = await context.Set<GuildGroupGameRecord>().Where(row => row.GuildId == scope).ToListAsync();
        return rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(row => new GroupGame(row.Id, row.GroupId, row.Name, row.Provider, row.ExternalId)).ToArray();
    }

    public async Task<bool> SaveGameAsync(ulong guildId, GroupGame game)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        string scope = Scope(guildId);
        var games = context.Set<GuildGroupGameRecord>();
        if (game.ExternalId is not null && await games.AnyAsync(row => row.GuildId == scope && row.GroupId == game.GroupId &&
                row.Provider == game.Provider && row.ExternalId == game.ExternalId && row.Id != game.Id))
            return false;

        var row = await games.FirstOrDefaultAsync(item => item.Id == game.Id && item.GuildId == scope && item.GroupId == game.GroupId);
        if (row is null)
        {
            games.Add(new GuildGroupGameRecord
            {
                Id = game.Id,
                GuildId = scope,
                GroupId = game.GroupId,
                Name = game.Name,
                Provider = game.Provider,
                ExternalId = game.ExternalId,
                AddedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            row.Name = game.Name;
            row.Provider = game.Provider;
            row.ExternalId = game.ExternalId;
        }

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The unique index caught a concurrent duplicate.
            return false;
        }
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> RemoveGameAsync(ulong guildId, Guid groupId, Guid gameId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string scope = Scope(guildId);
        var row = await context.Set<GuildGroupGameRecord>()
            .FirstOrDefaultAsync(item => item.Id == gameId && item.GuildId == scope && item.GroupId == groupId);
        if (row is null)
            return false;

        context.Remove(row);
        await context.SaveChangesAsync();
        return true;
    }

    private static string Scope(ulong guildId) => guildId.ToString(CultureInfo.InvariantCulture);

    private static GuildGroup ToGroup(GuildGroupRecord row) =>
        JsonSerializer.Deserialize<GuildGroup>(row.Data)! with { Id = row.GroupId ?? Guid.Empty };

    // The durable ID lives in its own column; keep it out of the JSON document so there is one source of truth.
    private static string Serialize(GuildGroup group) => JsonSerializer.Serialize(group with { Id = Guid.Empty });
}

internal sealed class GuildGroupRecord
{
    public required string GuildId { get; init; }
    public required string NameKey { get; init; }
    public Guid? GroupId { get; set; }
    public required string Data { get; set; }
}

internal sealed class GuildGroupGameRecord
{
    public Guid Id { get; init; }
    public required string GuildId { get; init; }
    public Guid GroupId { get; init; }
    public required string Name { get; set; }
    public string? Provider { get; set; }
    public string? ExternalId { get; set; }
    public DateTime AddedAtUtc { get; init; }
}
