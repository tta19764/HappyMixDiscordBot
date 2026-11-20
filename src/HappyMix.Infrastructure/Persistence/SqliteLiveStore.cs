using System.Globalization;
using HappyMix.Application.Abstractions.Live;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Live choices, sessions and cooldowns in the LiveMembers, LiveGroups, PlaySessions and LiveAlerts tables.</summary>
internal sealed class SqliteLiveStore(IDbContextFactory<HappyMixDbContext> factory) : ILiveStore
{
    public async Task<IReadOnlyList<LiveMembership>> ListMembershipsAsync(ulong guildId, Guid groupId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId.ToString();
        return (await db.Database.SqlQuery<MemberRow>($"SELECT * FROM LiveMembers WHERE GuildId = {guild} AND GroupId = {group}").ToListAsync())
            .Select(ToMembership).ToArray();
    }

    public async Task<IReadOnlyList<LiveMembership>> ListSharingAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        return (await db.Database.SqlQuery<MemberRow>($"SELECT * FROM LiveMembers WHERE UserId = {user} AND Share = 1").ToListAsync())
            .Select(ToMembership).ToArray();
    }

    public async Task<IReadOnlyList<ulong>> ListSharersAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Database.SqlQuery<string>($"SELECT DISTINCT UserId AS Value FROM LiveMembers WHERE Share = 1").ToListAsync())
            .Select(Parse).ToArray();
    }

    public async Task SaveMembershipAsync(LiveMembership membership)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO LiveMembers (GuildId, GroupId, UserId, Share, Notify)
            VALUES ({Id(membership.GuildId)}, {membership.GroupId.ToString()}, {Id(membership.UserId)}, {(membership.Share ? 1 : 0)}, {(membership.Notify ? 1 : 0)})
            ON CONFLICT (GuildId, GroupId, UserId) DO UPDATE SET Share = excluded.Share, Notify = excluded.Notify
            """);
    }

    public async Task<bool> GetChannelPostsAsync(ulong guildId, Guid groupId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId.ToString();
        return await db.Database.SqlQuery<long>($"SELECT PostInChannel AS Value FROM LiveGroups WHERE GuildId = {guild} AND GroupId = {group}")
            .SingleOrDefaultAsync() == 1;
    }

    public async Task SetChannelPostsAsync(ulong guildId, Guid groupId, bool post)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO LiveGroups (GuildId, GroupId, PostInChannel) VALUES ({Id(guildId)}, {groupId.ToString()}, {(post ? 1 : 0)})
            ON CONFLICT (GuildId, GroupId) DO UPDATE SET PostInChannel = excluded.PostInChannel
            """);
    }

    public async Task<IReadOnlyList<PlaySession>> ListSessionsAsync(IReadOnlyCollection<ulong> userIds, DateTimeOffset since)
    {
        if (userIds.Count == 0)
            return [];
        await using var db = await factory.CreateDbContextAsync();
        long from = since.ToUnixTimeSeconds();
        var result = new List<PlaySession>();
        foreach (string user in userIds.Select(Id).Distinct())
            result.AddRange((await db.Database.SqlQuery<SessionRow>(
                    $"SELECT * FROM PlaySessions WHERE UserId = {user} AND (EndedUtc IS NULL OR EndedUtc >= {from})").ToListAsync())
                .Select(ToSession));
        return result;
    }

    public async Task<PlaySession?> GetOpenAsync(ulong userId, string source)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        var row = await db.Database.SqlQuery<SessionRow>(
                $"SELECT * FROM PlaySessions WHERE UserId = {user} AND Source = {source} AND EndedUtc IS NULL ORDER BY Id DESC LIMIT 1")
            .ToListAsync();
        return row.Count == 0 ? null : ToSession(row[0]);
    }

    public async Task<long> StartAsync(ulong userId, string game, string source, DateTimeOffset at)
    {
        await using var db = await factory.CreateDbContextAsync();
        // ToList, not Single: composing a query around INSERT … RETURNING isn't valid SQL.
        var ids = await db.Database.SqlQuery<long>(
            $"INSERT INTO PlaySessions (UserId, Game, Source, StartedUtc) VALUES ({Id(userId)}, {game}, {source}, {at.ToUnixTimeSeconds()}) RETURNING Id AS Value")
            .ToListAsync();
        return ids[0];
    }

    public async Task EndAsync(long sessionId, DateTimeOffset at)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PlaySessions SET EndedUtc = {at.ToUnixTimeSeconds()} WHERE Id = {sessionId} AND EndedUtc IS NULL");
    }

    public async Task<DateTimeOffset?> LastAlertAsync(ulong guildId, Guid groupId, ulong userId, string game)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId.ToString(), user = Id(userId);
        var sent = await db.Database.SqlQuery<long>(
                $"SELECT SentUtc AS Value FROM LiveAlerts WHERE GuildId = {guild} AND GroupId = {group} AND UserId = {user} AND Game = {game}")
            .ToListAsync();
        return sent.Count == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(sent[0]);
    }

    public async Task SaveAlertAsync(ulong guildId, Guid groupId, ulong userId, string game, DateTimeOffset at)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO LiveAlerts (GuildId, GroupId, UserId, Game, SentUtc) VALUES ({Id(guildId)}, {groupId.ToString()}, {Id(userId)}, {game}, {at.ToUnixTimeSeconds()})
            ON CONFLICT (GuildId, GroupId, UserId, Game) DO UPDATE SET SentUtc = excluded.SentUtc
            """);
    }

    public async Task PruneAsync(DateTimeOffset before)
    {
        await using var db = await factory.CreateDbContextAsync();
        long cutoff = before.ToUnixTimeSeconds();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM PlaySessions WHERE EndedUtc IS NOT NULL AND EndedUtc < {cutoff}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM LiveAlerts WHERE SentUtc < {cutoff}");
    }

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private static LiveMembership ToMembership(MemberRow row) =>
        new(Parse(row.GuildId), Guid.Parse(row.GroupId), Parse(row.UserId), row.Share != 0, row.Notify != 0);

    private static PlaySession ToSession(SessionRow row) => new(row.Id, Parse(row.UserId), row.Game, row.Source,
        DateTimeOffset.FromUnixTimeSeconds(row.StartedUtc), row.EndedUtc is { } ended ? DateTimeOffset.FromUnixTimeSeconds(ended) : null);

    private sealed record MemberRow(string GuildId, string GroupId, string UserId, long Share, long Notify);
    private sealed record SessionRow(long Id, string UserId, string Game, string Source, long StartedUtc, long? EndedUtc);
}
