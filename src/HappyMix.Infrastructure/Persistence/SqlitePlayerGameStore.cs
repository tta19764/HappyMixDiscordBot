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
        var row = await db.Database.SqlQuery<SteamRow>($"SELECT * FROM SteamAccounts WHERE UserId = {user}").SingleOrDefaultAsync();
        return row is null ? null : ToAccount(row);
    }

    public async Task<SteamAccount?> FindSteamOwnerAsync(string steamId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Database.SqlQuery<SteamRow>($"SELECT * FROM SteamAccounts WHERE SteamId = {steamId}").SingleOrDefaultAsync();
        return row is null ? null : ToAccount(row);
    }

    public async Task SaveSteamAsync(SteamAccount account)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO SteamAccounts (UserId, SteamId, Verified, LinkedUtc, ImportedUtc)
            VALUES ({Id(account.UserId)}, {account.SteamId}, {(account.Verified ? 1 : 0)}, {account.LinkedAt.ToUnixTimeSeconds()}, {account.ImportedAt?.ToUnixTimeSeconds()})
            ON CONFLICT (UserId) DO UPDATE SET SteamId = excluded.SteamId, Verified = excluded.Verified, LinkedUtc = excluded.LinkedUtc,
                ImportedUtc = excluded.ImportedUtc
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
        DateTimeOffset.FromUnixTimeSeconds(row.LinkedUtc), row.ImportedUtc is { } imported ? DateTimeOffset.FromUnixTimeSeconds(imported) : null);

    private static PlayerGame ToGame(GameRow row) => new(Parse(row.UserId), row.GameKey, row.Name, row.Provider, row.ExternalId,
        (GamePreference)row.Preference, row.FromSteam != 0, row.PlaytimeMinutes, row.RecentMinutes, DateTimeOffset.FromUnixTimeSeconds(row.UpdatedUtc));

    private sealed record SteamRow(string UserId, string SteamId, long Verified, long LinkedUtc, long? ImportedUtc);

    private sealed record GameRow(string UserId, string GameKey, string Name, string? Provider, string? ExternalId, long Preference,
        long FromSteam, int? PlaytimeMinutes, int? RecentMinutes, long UpdatedUtc);
}
