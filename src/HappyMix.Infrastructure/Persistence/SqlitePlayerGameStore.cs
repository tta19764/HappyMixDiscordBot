using System.Globalization;
using HappyMix.Application.Abstractions.Players;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Steam links and game lists in the SteamAccounts and PlayerGames tables (see migration AddPlayerGames).</summary>
internal sealed class SqlitePlayerGameStore(IDbContextFactory<HappyMixDbContext> factory) : IPlayerGameStore
{
    public async Task<SteamAccount?> GetSteamAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        var row = await db.Database.SqlQuery<SteamRow>(
            $"SELECT * FROM SteamAccounts WHERE UserId = {user} ORDER BY IsMain DESC, LinkedUtc, SteamId LIMIT 1").FirstOrDefaultAsync();
        return row is null ? null : ToAccount(row);
    }

    public async Task<IReadOnlyList<SteamAccount>> ListSteamAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        return (await db.Database.SqlQuery<SteamRow>($"SELECT * FROM SteamAccounts WHERE UserId = {user} ORDER BY IsMain DESC, LinkedUtc, SteamId")
            .ToListAsync()).Select(ToAccount).ToArray();
    }

    public async Task RemoveSteamAccountAsync(ulong userId, string steamId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        string user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM SteamAccounts WHERE UserId = {user} AND SteamId = {steamId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE SteamAccounts SET IsMain = 1 WHERE UserId = {user} AND NOT EXISTS (SELECT 1 FROM SteamAccounts WHERE UserId = {user} AND IsMain = 1)
                AND SteamId = (SELECT SteamId FROM SteamAccounts WHERE UserId = {user} ORDER BY LinkedUtc, SteamId LIMIT 1)
            """);
        await transaction.CommitAsync();
    }

    public async Task SetMainSteamAsync(ulong userId, string steamId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE SteamAccounts SET IsMain = CASE WHEN SteamId = {steamId} THEN 1 ELSE 0 END
            WHERE UserId = {user} AND EXISTS (SELECT 1 FROM SteamAccounts WHERE UserId = {user} AND SteamId = {steamId})
            """);
    }

    public async Task<SteamAccount?> FindSteamOwnerAsync(string steamId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Database.SqlQuery<SteamRow>($"SELECT * FROM SteamAccounts WHERE SteamId = {steamId}").FirstOrDefaultAsync();
        return row is null ? null : ToAccount(row);
    }

    public async Task SaveSteamAsync(SteamAccount account)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO SteamAccounts (UserId, SteamId, Verified, LinkedUtc, ImportedUtc, IsMain, ShowStats, Label)
            VALUES ({Id(account.UserId)}, {account.SteamId}, {(account.Verified ? 1 : 0)}, {account.LinkedAt.ToUnixTimeSeconds()}, {account.ImportedAt?.ToUnixTimeSeconds()},
                    {(account.IsMain ? 1 : 0)}, {(account.ShowStats ? 1 : 0)}, {account.Label})
            ON CONFLICT (UserId, SteamId) DO UPDATE SET Verified = excluded.Verified, LinkedUtc = excluded.LinkedUtc,
                ImportedUtc = excluded.ImportedUtc, IsMain = excluded.IsMain, ShowStats = excluded.ShowStats, Label = excluded.Label
            """);
    }

    public async Task RemoveSteamAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        string user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM PlayerGames WHERE UserId = {user} AND FromSteam = 1");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM SteamAccounts WHERE UserId = {user}");
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<PlayerGame>> ListAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        return (await db.Database.SqlQuery<GameRow>($"SELECT * FROM PlayerGames WHERE UserId = {user}").ToListAsync()).Select(ToGame).ToArray();
    }

    public async Task<IReadOnlyList<PlayerGame>> ListForAsync(IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0)
            return [];
        await using var db = await factory.CreateDbContextAsync();
        var ids = userIds.Select(Id).ToHashSet(StringComparer.Ordinal);
        // Groups are small; filtering in memory keeps the SQL parameterised without building IN lists.
        var result = new List<PlayerGame>();
        foreach (string user in ids)
            result.AddRange((await db.Database.SqlQuery<GameRow>($"SELECT * FROM PlayerGames WHERE UserId = {user}").ToListAsync()).Select(ToGame));
        return result;
    }

    public async Task<IReadOnlyList<PlayerGame>> ListOwnersAsync(string gameKey, string gameName)
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Database.SqlQuery<GameRow>($"""
                SELECT * FROM PlayerGames WHERE (GameKey = {gameKey} OR Name = {gameName} COLLATE NOCASE)
                    AND (FromSteam = 1 OR PlaytimeMinutes IS NOT NULL) LIMIT 2000
                """).ToListAsync()).Select(ToGame).ToArray();
    }

    public Task SaveAsync(PlayerGame game) => SaveManyAsync([game]);

    public async Task SaveManyAsync(IReadOnlyCollection<PlayerGame> games)
    {
        if (games.Count == 0)
            return;
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var game in games)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO PlayerGames (UserId, GameKey, Name, Provider, ExternalId, Preference, FromSteam, PlaytimeMinutes, RecentMinutes, UpdatedUtc)
                VALUES ({Id(game.UserId)}, {game.Key}, {game.Name}, {game.Provider}, {game.ExternalId}, {(int)game.Preference}, {(game.FromSteam ? 1 : 0)},
                    {game.PlaytimeMinutes}, {game.RecentMinutes}, {game.UpdatedAt.ToUnixTimeSeconds()})
                ON CONFLICT (UserId, GameKey) DO UPDATE SET Name = excluded.Name, Provider = excluded.Provider, ExternalId = excluded.ExternalId,
                    Preference = excluded.Preference, FromSteam = excluded.FromSteam, PlaytimeMinutes = excluded.PlaytimeMinutes,
                    RecentMinutes = excluded.RecentMinutes, UpdatedUtc = excluded.UpdatedUtc
                """);
        await transaction.CommitAsync();
    }

    public async Task<bool> RemoveAsync(ulong userId, string key)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        return await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM PlayerGames WHERE UserId = {user} AND GameKey = {key}") == 1;
    }

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private static SteamAccount ToAccount(SteamRow row) => new(Parse(row.UserId), row.SteamId, row.Verified != 0,
        DateTimeOffset.FromUnixTimeSeconds(row.LinkedUtc), row.ImportedUtc is { } imported ? DateTimeOffset.FromUnixTimeSeconds(imported) : null)
    {
        IsMain = row.IsMain != 0,
        ShowStats = row.ShowStats != 0,
        Label = row.Label
    };

    private static PlayerGame ToGame(GameRow row) => new(Parse(row.UserId), row.GameKey, row.Name, row.Provider, row.ExternalId,
        (GamePreference)row.Preference, row.FromSteam != 0, row.PlaytimeMinutes, row.RecentMinutes, DateTimeOffset.FromUnixTimeSeconds(row.UpdatedUtc));

    private sealed record SteamRow(string UserId, string SteamId, long Verified, long LinkedUtc, long? ImportedUtc, long IsMain, long ShowStats, string? Label);

    private sealed record GameRow(string UserId, string GameKey, string Name, string? Provider, string? ExternalId, long Preference,
        long FromSteam, int? PlaytimeMinutes, int? RecentMinutes, long UpdatedUtc);
}
