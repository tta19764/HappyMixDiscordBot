using System.Globalization;
using System.Text.Json;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Infrastructure.PandaScore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HappyMix.Infrastructure.Persistence;

internal sealed class SqliteGuildSettingsStore(
    IDbContextFactory<HappyMixDbContext> factory, IOptions<PandaScoreOptions> pandaScore) : IGuildSettingsStore
{
    public async Task<GuildSettings?> GetAsync(ulong guildId, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var row = await context.Set<GuildSettingsRecord>().FindAsync(
            [guildId.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        return row is null ? null : ToSettings(row);
    }

    public async Task SaveAsync(GuildSettings settings, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await SaveAsync(context, settings, cancellationToken);
    }

    public async Task<IReadOnlyList<GuildSettings>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Set<GuildSettingsRecord>().AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(ToSettings).ToArray();
    }

    public async Task UpdateAsync(ulong guildId, string defaultTimeZone, Func<GuildSettings, GuildSettings> update,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var row = await context.Set<GuildSettingsRecord>().FindAsync(
            [guildId.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        var settings = row is null ? new GuildSettings(guildId, 0, defaultTimeZone) : ToSettings(row);
        await SaveAsync(context, update(settings), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static GuildSettings ToSettings(GuildSettingsRecord row) => new(
        ulong.Parse(row.GuildId, CultureInfo.InvariantCulture),
        ulong.Parse(row.ChannelId, CultureInfo.InvariantCulture), row.TimeZoneId,
        row.Preferences is null ? null : JsonSerializer.Deserialize<GuildEsportsPreferences>(row.Preferences));

    private async Task SaveAsync(HappyMixDbContext context, GuildSettings settings, CancellationToken cancellationToken)
    {
        var defaults = pandaScore.Value;
        var preferences = settings.Preferences;
        if (defaults.CounterStrikeTeamAliases.Count > 0 || defaults.Dota2TeamAliases.Count > 0)
        {
            preferences ??= GuildEsportsPreferences.Default;
            preferences = preferences with
            {
                CounterStrikeTeamAliases = preferences.CounterStrikeTeamAliases ?? defaults.CounterStrikeTeamAliases,
                Dota2TeamAliases = preferences.Dota2TeamAliases ?? defaults.Dota2TeamAliases
            };
        }
        string? json = preferences is null ? null : JsonSerializer.Serialize(preferences);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GuildSettings" ("GuildId", "ChannelId", "TimeZoneId", "Preferences")
            VALUES ({settings.GuildId.ToString(CultureInfo.InvariantCulture)},
                    {settings.ChannelId.ToString(CultureInfo.InvariantCulture)}, {settings.TimeZoneId}, {json})
            ON CONFLICT ("GuildId") DO UPDATE SET
                "ChannelId" = excluded."ChannelId", "TimeZoneId" = excluded."TimeZoneId",
                "Preferences" = excluded."Preferences"
            """, cancellationToken);
    }
}

internal sealed class GuildSettingsRecord
{
    public required string GuildId { get; init; }
    public required string ChannelId { get; init; }
    public required string TimeZoneId { get; init; }
    public string? Preferences { get; init; }
}
