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
        string scope = guildId.ToString(CultureInfo.InvariantCulture);
        var rows = await context.Set<GuildGroupRecord>().Where(row => row.GuildId == scope)
            .OrderBy(row => row.NameKey).Select(row => row.Data).ToListAsync();
        return rows.Select(data => JsonSerializer.Deserialize<GuildGroup>(data)!).ToArray();
    }

    public async Task UpdateAsync(ulong guildId, string name, Func<GuildGroup?, GuildGroup?> update)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        string scope = guildId.ToString(CultureInfo.InvariantCulture);
        string key = name.Trim().ToUpperInvariant();
        var rows = context.Set<GuildGroupRecord>();
        var row = await rows.FindAsync(scope, key);
        var group = update(row is null ? null : JsonSerializer.Deserialize<GuildGroup>(row.Data));
        if (group is null)
        {
            if (row is not null)
                rows.Remove(row);
        }
        else if (row is null)
        {
            rows.Add(new GuildGroupRecord { GuildId = scope, NameKey = key, Data = JsonSerializer.Serialize(group) });
        }
        else
        {
            row.Data = JsonSerializer.Serialize(group);
        }

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}

internal sealed class GuildGroupRecord
{
    public required string GuildId { get; init; }
    public required string NameKey { get; init; }
    public required string Data { get; set; }
}
