using System.Globalization;
using HappyMix.Application.Abstractions.Profiles;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Discord profiles saved at sign-in, in the UserProfiles table.</summary>
internal sealed class SqliteUserProfileStore(IDbContextFactory<HappyMixDbContext> factory) : IUserProfileStore
{
    public async Task<UserProfile?> GetAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = userId.ToString(CultureInfo.InvariantCulture);
        var row = await db.Database.SqlQuery<ProfileRow>($"SELECT * FROM UserProfiles WHERE UserId = {user}").SingleOrDefaultAsync();
        return row is null ? null : new UserProfile(userId, row.UserName, row.DisplayName, row.Avatar,
            row.SteamId is { } steamId ? new DiscordSteamConnection(steamId, row.SteamName ?? "", row.SteamVerified == 1) : null,
            row.ConnectionsChecked == 1, DateTimeOffset.FromUnixTimeMilliseconds(row.SyncedUtc));
    }

    public async Task SaveAsync(UserProfile profile)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO UserProfiles (UserId, UserName, DisplayName, Avatar, SteamId, SteamName, SteamVerified, ConnectionsChecked, SyncedUtc)
            VALUES ({profile.UserId.ToString(CultureInfo.InvariantCulture)}, {profile.UserName}, {profile.DisplayName}, {profile.Avatar},
                {profile.Steam?.SteamId}, {profile.Steam?.Name}, {(profile.Steam?.Verified == true ? 1 : 0)}, {(profile.ConnectionsChecked ? 1 : 0)},
                {profile.SyncedAt.ToUnixTimeMilliseconds()})
            ON CONFLICT (UserId) DO UPDATE SET UserName = excluded.UserName, DisplayName = excluded.DisplayName, Avatar = excluded.Avatar,
                SteamId = excluded.SteamId, SteamName = excluded.SteamName, SteamVerified = excluded.SteamVerified,
                ConnectionsChecked = excluded.ConnectionsChecked, SyncedUtc = excluded.SyncedUtc
            """);
    }

    private sealed record ProfileRow(string UserId, string UserName, string? DisplayName, string? Avatar, string? SteamId, string? SteamName,
        long SteamVerified, long ConnectionsChecked, long SyncedUtc);
}
