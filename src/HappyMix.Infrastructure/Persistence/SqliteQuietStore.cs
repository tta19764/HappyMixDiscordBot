using System.Globalization;
using HappyMix.Application.Abstractions.Quiet;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Do-not-disturb ranges (QuietWeekly, QuietDates) and messages waiting for them to end (HeldMessages).</summary>
internal sealed class SqliteQuietStore(IDbContextFactory<HappyMixDbContext> factory) : IQuietStore
{
    public async Task<QuietSchedule> GetAsync(ulong userId) =>
        (await GetManyAsync([userId])).GetValueOrDefault(userId) ?? QuietSchedule.Empty;

    public async Task<IReadOnlyDictionary<ulong, QuietSchedule>> GetManyAsync(IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0)
            return new Dictionary<ulong, QuietSchedule>();
        await using var db = await factory.CreateDbContextAsync();
        string ids = System.Text.Json.JsonSerializer.Serialize(userIds.Select(Id).Distinct());
        var weekly = await db.Database.SqlQuery<WeeklyRow>(
            $"SELECT * FROM QuietWeekly WHERE UserId IN (SELECT value FROM json_each({ids}))").ToListAsync();
        var dates = await db.Database.SqlQuery<DatesRow>(
            $"SELECT * FROM QuietDates WHERE UserId IN (SELECT value FROM json_each({ids}))").ToListAsync();
        var byWeekly = weekly.ToLookup(row => Parse(row.UserId));
        var byDates = dates.ToLookup(row => Parse(row.UserId));
        return userIds.Distinct().Where(user => byWeekly.Contains(user) || byDates.Contains(user)).ToDictionary(user => user, user => new QuietSchedule(
            byWeekly[user].Select(row => new QuietWeekly(Guid.Parse(row.Id), (DayOfWeek)row.Day, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(row.StartMinute)),
                    TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(row.EndMinute)), row.TimeZone))
                .OrderBy(range => ((int)range.Day + 6) % 7).ThenBy(range => range.Start).ToArray(),
            byDates[user].Select(row => new QuietDates(Guid.Parse(row.Id), DateTimeOffset.FromUnixTimeSeconds(row.StartUtc),
                DateTimeOffset.FromUnixTimeSeconds(row.EndUtc))).OrderBy(range => range.Start).ToArray()));
    }

    public async Task AddWeeklyAsync(ulong userId, IReadOnlyCollection<QuietWeekly> ranges)
    {
        await using var db = await factory.CreateDbContextAsync();
        foreach (var range in ranges)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO QuietWeekly (Id, UserId, Day, StartMinute, EndMinute, TimeZone)
                VALUES ({range.Id.ToString()}, {Id(userId)}, {(int)range.Day}, {(int)range.Start.ToTimeSpan().TotalMinutes},
                    {(int)range.End.ToTimeSpan().TotalMinutes}, {range.TimeZone})
                """);
    }

    public async Task AddDatesAsync(ulong userId, IReadOnlyCollection<QuietDates> ranges)
    {
        await using var db = await factory.CreateDbContextAsync();
        foreach (var range in ranges)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO QuietDates (Id, UserId, StartUtc, EndUtc)
                VALUES ({range.Id.ToString()}, {Id(userId)}, {range.Start.ToUnixTimeSeconds()}, {range.End.ToUnixTimeSeconds()})
                """);
    }

    public async Task<bool> RemoveAsync(ulong userId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId), key = id.ToString();
        int removed = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM QuietWeekly WHERE UserId = {user} AND Id = {key}")
            + await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM QuietDates WHERE UserId = {user} AND Id = {key}");
        return removed > 0;
    }

    public async Task PruneDatesAsync(DateTimeOffset before)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM QuietDates WHERE EndUtc < {before.ToUnixTimeSeconds()}");
    }

    public async Task HoldAsync(ulong userId, string kind, string? key, string payload, DateTimeOffset heldAt, DateTimeOffset releaseAt)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        if (key is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM HeldMessages WHERE UserId = {user} AND Kind = {kind} AND Key = {key}");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO HeldMessages (UserId, Kind, Key, Payload, HeldUtc, ReleaseUtc)
            VALUES ({user}, {kind}, {key}, {payload}, {heldAt.ToUnixTimeSeconds()}, {releaseAt.ToUnixTimeSeconds()})
            """);
    }

    public async Task<IReadOnlyList<HeldMessage>> DueAsync(DateTimeOffset now, int limit)
    {
        await using var db = await factory.CreateDbContextAsync();
        long at = now.ToUnixTimeSeconds();
        return (await db.Database.SqlQuery<HeldRow>($"SELECT * FROM HeldMessages WHERE ReleaseUtc <= {at} ORDER BY Id LIMIT {limit}").ToListAsync())
            .Select(row => new HeldMessage(row.Id, Parse(row.UserId), row.Kind, row.Key, row.Payload, DateTimeOffset.FromUnixTimeSeconds(row.HeldUtc),
                DateTimeOffset.FromUnixTimeSeconds(row.ReleaseUtc)))
            .ToArray();
    }

    public async Task<int> CountHeldAsync(ulong userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string user = Id(userId);
        return (int)await db.Database.SqlQuery<long>($"SELECT COUNT(*) AS Value FROM HeldMessages WHERE UserId = {user}").SingleAsync();
    }

    public async Task RescheduleAsync(long id, DateTimeOffset releaseAt)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE HeldMessages SET ReleaseUtc = {releaseAt.ToUnixTimeSeconds()} WHERE Id = {id}");
    }

    public async Task RemoveHeldAsync(long id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM HeldMessages WHERE Id = {id}");
    }

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private sealed record WeeklyRow(string Id, string UserId, long Day, long StartMinute, long EndMinute, string TimeZone);
    private sealed record DatesRow(string Id, string UserId, long StartUtc, long EndUtc);
    private sealed record HeldRow(long Id, string UserId, string Kind, string? Key, string Payload, long HeldUtc, long ReleaseUtc);
}
