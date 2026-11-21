using System.Globalization;
using System.Text.Json;
using HappyMix.Application.Abstractions.Stats;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Stats sharing choices (GameStatsSharing) and fetched cards (GameStatsCache).</summary>
internal sealed class SqliteGameStatsStore(IDbContextFactory<HappyMixDbContext> factory) : IGameStatsStore
{
    public async Task<IReadOnlySet<ulong>> ListSharingAsync(IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0)
            return new HashSet<ulong>();
        await using var db = await factory.CreateDbContextAsync();
        string ids = Ids(userIds);
        return (await db.Database.SqlQuery<string>(
                $"SELECT UserId AS Value FROM GameStatsSharing WHERE Share = 1 AND UserId IN (SELECT value FROM json_each({ids}))").ToListAsync())
            .Select(Parse).ToHashSet();
    }

    public async Task<IReadOnlyList<ulong>> ListAllSharingAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Database.SqlQuery<string>($"SELECT UserId AS Value FROM GameStatsSharing WHERE Share = 1").ToListAsync()).Select(Parse).ToArray();
    }

    public async Task SetSharingAsync(ulong userId, bool share, DateTimeOffset at)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO GameStatsSharing (UserId, Share, ChangedUtc) VALUES ({Id(userId)}, {(share ? 1 : 0)}, {at.ToUnixTimeSeconds()})
            ON CONFLICT (UserId) DO UPDATE SET Share = excluded.Share, ChangedUtc = excluded.ChangedUtc
            """);
    }

    public async Task<IReadOnlyList<CachedStats>> ListCachedAsync(IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0)
            return [];
        await using var db = await factory.CreateDbContextAsync();
        string ids = Ids(userIds);
        return (await db.Database.SqlQuery<CacheRow>($"SELECT * FROM GameStatsCache WHERE UserId IN (SELECT value FROM json_each({ids}))").ToListAsync())
            .Where(row => Enum.TryParse<StatsGame>(row.Game, out _) && Enum.TryParse<StatsStatus>(row.Status, out _))
            .Select(row => new CachedStats(Parse(row.UserId), Enum.Parse<StatsGame>(row.Game), Enum.Parse<StatsStatus>(row.Status), row.Payload,
                DateTimeOffset.FromUnixTimeSeconds(row.FetchedUtc)))
            .ToArray();
    }

    public async Task SaveCachedAsync(CachedStats stats)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO GameStatsCache (UserId, Game, Status, Payload, FetchedUtc)
            VALUES ({Id(stats.UserId)}, {stats.Game.ToString()}, {stats.Status.ToString()}, {stats.Payload}, {stats.FetchedAt.ToUnixTimeSeconds()})
            ON CONFLICT (UserId, Game) DO UPDATE SET Status = excluded.Status, Payload = excluded.Payload, FetchedUtc = excluded.FetchedUtc
            """);
    }

    public async Task RemoveCachedAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GameStatsCache WHERE UserId = {user}");
    }

    private static string Ids(IEnumerable<ulong> userIds) => JsonSerializer.Serialize(userIds.Select(Id).Distinct());
    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private sealed record CacheRow(string UserId, string Game, string Status, string? Payload, long FetchedUtc);
}
