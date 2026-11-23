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
                DateTimeOffset.FromUnixTimeSeconds(row.FetchedUtc), row.Reason) { SteamId = row.SteamId.Length == 0 ? null : row.SteamId })
            .ToArray();
    }

    public async Task SaveCachedAsync(CachedStats stats)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO GameStatsCache (UserId, SteamId, Game, Status, Payload, FetchedUtc, Reason)
            VALUES ({Id(stats.UserId)}, {stats.SteamId ?? ""}, {stats.Game.ToString()}, {stats.Status.ToString()}, {stats.Payload}, {stats.FetchedAt.ToUnixTimeSeconds()}, {stats.Reason})
            ON CONFLICT (UserId, SteamId, Game) DO UPDATE SET Status = excluded.Status, Payload = excluded.Payload, FetchedUtc = excluded.FetchedUtc,
                Reason = excluded.Reason
            """);
    }

    public async Task RemoveCachedAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GameStatsCache WHERE UserId = {user}");
    }

    public async Task RemoveCachedAsync(ulong userId, string steamId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GameStatsCache WHERE UserId = {user} AND SteamId = {steamId}");
    }

    public async Task SaveMatchesAsync(IReadOnlyCollection<PlayerMatch> matches)
    {
        if (matches.Count == 0)
            return;
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var match in matches)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO PlayerMatches (UserId, Game, MatchId, StartedUtc, Duration, Won, Pick, Image, Kills, Deaths, Assists, Headshots, Score)
                VALUES ({Id(match.UserId)}, {match.Game.ToString()}, {match.MatchId}, {match.StartedAt.ToUnixTimeSeconds()}, {match.DurationSeconds},
                    {(match.Won ? 1 : 0)}, {match.Pick}, {match.Image}, {match.Kills}, {match.Deaths}, {match.Assists}, {match.Headshots}, {match.Score})
                """);
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<PlayerMatch>> ListMatchesAsync(IReadOnlyCollection<ulong> userIds, DateTimeOffset since)
    {
        if (userIds.Count == 0)
            return [];
        await using var db = await factory.CreateDbContextAsync();
        string ids = Ids(userIds);
        long from = since.ToUnixTimeSeconds();
        return (await db.Database.SqlQuery<MatchRow>(
                $"SELECT * FROM PlayerMatches WHERE StartedUtc >= {from} AND UserId IN (SELECT value FROM json_each({ids}))").ToListAsync())
            .Where(row => Enum.TryParse<StatsGame>(row.Game, out _))
            .Select(row => new PlayerMatch(Parse(row.UserId), Enum.Parse<StatsGame>(row.Game), row.MatchId, DateTimeOffset.FromUnixTimeSeconds(row.StartedUtc),
                (int)row.Duration, row.Won == 1, row.Pick, row.Image, (int)row.Kills, (int)row.Deaths, (int)row.Assists, row.Headshots, row.Score))
            .ToArray();
    }

    public async Task<DateTimeOffset?> MatchesFetchedAtAsync(ulong userId, StatsGame game)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId), kind = game.ToString();
        var at = await db.Database.SqlQuery<long>($"SELECT FetchedUtc AS Value FROM MatchFetches WHERE UserId = {user} AND Game = {kind}").ToListAsync();
        return at.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(at[0]) : null;
    }

    public async Task SetMatchesFetchedAtAsync(ulong userId, StatsGame game, DateTimeOffset at)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO MatchFetches (UserId, Game, FetchedUtc) VALUES ({Id(userId)}, {game.ToString()}, {at.ToUnixTimeSeconds()})
            ON CONFLICT (UserId, Game) DO UPDATE SET FetchedUtc = excluded.FetchedUtc
            """);
    }

    public async Task RemoveMatchesAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM PlayerMatches WHERE UserId = {user}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM MatchFetches WHERE UserId = {user}");
    }

    public async Task PruneMatchesAsync(DateTimeOffset before)
    {
        await using var db = await factory.CreateDbContextAsync();
        long at = before.ToUnixTimeSeconds();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM PlayerMatches WHERE StartedUtc < {at}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GroupRecaps WHERE HandledUtc < {at}");
    }

    public async Task<bool> MarkRecapAsync(ulong guildId, Guid groupId, StatsGame game, string matchId, DateTimeOffset at)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO GroupRecaps (GuildId, GroupId, Game, MatchId, HandledUtc)
            VALUES ({Id(guildId)}, {groupId.ToString()}, {game.ToString()}, {matchId}, {at.ToUnixTimeSeconds()})
            """) > 0;
    }

    public async Task<bool> GetRecapChannelAsync(ulong guildId, Guid groupId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId.ToString();
        return await db.Database.SqlQuery<long>($"SELECT PostInChannel AS Value FROM RecapChannels WHERE GuildId = {guild} AND GroupId = {group}")
            .SingleOrDefaultAsync() == 1;
    }

    public async Task SetRecapChannelAsync(ulong guildId, Guid groupId, bool post)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RecapChannels (GuildId, GroupId, PostInChannel) VALUES ({Id(guildId)}, {groupId.ToString()}, {(post ? 1 : 0)})
            ON CONFLICT (GuildId, GroupId) DO UPDATE SET PostInChannel = excluded.PostInChannel
            """);
    }

    public async Task<bool> GetDigestChannelAsync(ulong guildId, Guid groupId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId.ToString();
        return await db.Database.SqlQuery<long>($"SELECT PostDigest AS Value FROM DigestChannels WHERE GuildId = {guild} AND GroupId = {group}")
            .SingleOrDefaultAsync() == 1;
    }

    public async Task SetDigestChannelAsync(ulong guildId, Guid groupId, bool post)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO DigestChannels (GuildId, GroupId, PostDigest) VALUES ({Id(guildId)}, {groupId.ToString()}, {(post ? 1 : 0)})
            ON CONFLICT (GuildId, GroupId) DO UPDATE SET PostDigest = excluded.PostDigest
            """);
    }

    public async Task<bool> MarkDigestAsync(ulong guildId, Guid groupId, DateOnly weekStart)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO GroupDigests (GuildId, GroupId, WeekStart)
            VALUES ({Id(guildId)}, {groupId.ToString()}, {weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})
            """) > 0;
    }

    private static string Ids(IEnumerable<ulong> userIds) => JsonSerializer.Serialize(userIds.Select(Id).Distinct());
    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private sealed record MatchRow(string UserId, string Game, string MatchId, long StartedUtc, long Duration, long Won, string? Pick, string? Image,
        long Kills, long Deaths, long Assists, double? Headshots, string? Score);
    private sealed record CacheRow(string UserId, string SteamId, string Game, string Status, string? Payload, long FetchedUtc, string? Reason);
}
