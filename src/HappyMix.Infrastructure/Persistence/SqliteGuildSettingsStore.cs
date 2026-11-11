using System.Globalization;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

internal sealed class SqliteGuildSettingsStore(IDbContextFactory<HappyMixDbContext> factory) : IGuildSettingsStore
{
    public async Task<GuildSettings?> GetAsync(ulong guildId, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var row = await context.Set<GuildSettingsRecord>().FindAsync(
            [guildId.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        return row is null ? null : new GuildSettings(guildId,
            ulong.Parse(row.ChannelId, CultureInfo.InvariantCulture), row.TimeZoneId);
    }

    public async Task SaveAsync(GuildSettings settings, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GuildSettings" ("GuildId", "ChannelId", "TimeZoneId")
            VALUES ({settings.GuildId.ToString(CultureInfo.InvariantCulture)},
                    {settings.ChannelId.ToString(CultureInfo.InvariantCulture)}, {settings.TimeZoneId})
            ON CONFLICT ("GuildId") DO UPDATE SET
                "ChannelId" = excluded."ChannelId", "TimeZoneId" = excluded."TimeZoneId"
            """, cancellationToken);
    }
}

internal sealed class GuildSettingsRecord
{
    public required string GuildId { get; init; }
    public required string ChannelId { get; init; }
    public required string TimeZoneId { get; init; }
}
