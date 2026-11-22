using System.Globalization;
using HappyMix.Application.Abstractions.Stats;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Game feature settings per server (DotaMetaSettings) and shared caches (FeatureCache).</summary>
internal sealed class SqliteGameFeatureStore(IDbContextFactory<HappyMixDbContext> factory) : IGameFeatureStore
{
    public async Task<DotaMetaSettings> GetDotaMetaAsync(ulong guildId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId);
        var row = (await db.Database.SqlQuery<MetaRow>($"SELECT * FROM DotaMetaSettings WHERE GuildId = {guild}").ToListAsync()).FirstOrDefault();
        return row is null || !Enum.TryParse<MetaSchedule>(row.Schedule, out var schedule)
            ? DotaMetaSettings.Default(guildId)
            : new DotaMetaSettings(guildId, schedule, row.ChannelId is { } channel ? ulong.Parse(channel, CultureInfo.InvariantCulture) : null,
                (DayOfWeek)row.Day, (int)row.Hour, row.LastPostedUtc is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null, (int)row.TopCount,
                Enum.TryParse<MetaRank>(row.MetaRank, out var rank) ? rank : MetaRank.All,
                Enum.TryParse<MetaPatch>(row.MetaPatch, out var patch) ? patch : MetaPatch.Current,
                Enum.TryParse<MetaSort>(row.MetaSort, out var sort) ? sort : MetaSort.Score);
    }

    public async Task SaveDotaMetaAsync(DotaMetaSettings settings)
    {
        await using var db = await factory.CreateDbContextAsync();
        string? channel = settings.ChannelId?.ToString(CultureInfo.InvariantCulture);
        long? last = settings.LastPostedAt?.ToUnixTimeSeconds();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO DotaMetaSettings (GuildId, Schedule, ChannelId, Day, Hour, LastPostedUtc, TopCount, MetaRank, MetaPatch, MetaSort)
            VALUES ({Id(settings.GuildId)}, {settings.Schedule.ToString()}, {channel}, {(int)settings.Day}, {settings.Hour}, {last}, {settings.Count},
                {settings.Rank.ToString()}, {settings.Patch.ToString()}, {settings.Sort.ToString()})
            ON CONFLICT (GuildId) DO UPDATE SET Schedule = excluded.Schedule, ChannelId = excluded.ChannelId, Day = excluded.Day,
                Hour = excluded.Hour, LastPostedUtc = excluded.LastPostedUtc, TopCount = excluded.TopCount, MetaRank = excluded.MetaRank,
                MetaPatch = excluded.MetaPatch, MetaSort = excluded.MetaSort
            """);
    }

    public async Task<(string Payload, DateTimeOffset FetchedAt)?> GetCachedAsync(string key)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = (await db.Database.SqlQuery<CacheRow>($"SELECT * FROM FeatureCache WHERE Key = {key}").ToListAsync()).FirstOrDefault();
        return row is null ? null : (row.Payload, DateTimeOffset.FromUnixTimeSeconds(row.FetchedUtc));
    }

    public async Task SaveCachedAsync(string key, string payload, DateTimeOffset fetchedAt)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO FeatureCache (Key, Payload, FetchedUtc) VALUES ({key}, {payload}, {fetchedAt.ToUnixTimeSeconds()})
            ON CONFLICT (Key) DO UPDATE SET Payload = excluded.Payload, FetchedUtc = excluded.FetchedUtc
            """);
    }

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record MetaRow(string GuildId, string Schedule, string? ChannelId, long Day, long Hour, long? LastPostedUtc, long TopCount,
        string MetaRank, string MetaPatch, string MetaSort);
    private sealed record CacheRow(string Key, string Payload, long FetchedUtc);
}
