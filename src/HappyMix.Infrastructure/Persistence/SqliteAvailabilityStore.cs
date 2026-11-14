using System.Globalization;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

internal sealed class SqliteAvailabilityStore(IDbContextFactory<HappyMixDbContext> factory) : IAvailabilityStore
{
    public async Task<IReadOnlyList<WeeklyAvailability>> ListWeeklyAsync(ulong guildId, ulong userId)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await WeeklyForMember(context, guildId, userId).OrderBy(row => (row.Day + 6) % 7).ThenBy(row => row.StartTicks)
            .Select(row => new WeeklyAvailability((DayOfWeek)row.Day, new TimeOnly(row.StartTicks),
                new TimeOnly(row.EndTicks), row.TimeZone, row.Tentative)).ToListAsync();
    }

    public async Task SaveWeeklyAsync(ulong guildId, ulong userId, WeeklyAvailability window)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = guildId.ToString(CultureInfo.InvariantCulture);
        string user = userId.ToString(CultureInfo.InvariantCulture);
        int day = (int)window.Day;
        long start = window.Start.Ticks;
        long end = window.End.Ticks;
        string zone = window.TimeZone;
        await using var transaction = await context.Database.BeginTransactionAsync();
        var others = await WeeklyForMember(context, guildId, userId).Where(row => row.TimeZone == zone).ToListAsync();
        long week = TimeSpan.TicksPerDay * 7;
        long from = day * TimeSpan.TicksPerDay + start;
        long until = from + (end > start ? end - start : TimeSpan.TicksPerDay + end - start);
        foreach (var other in others.Where(row => row.Day != day || row.StartTicks != start))
        {
            long otherStart = other.Day * TimeSpan.TicksPerDay + other.StartTicks;
            long otherEnd = otherStart + (other.EndTicks > other.StartTicks ? other.EndTicks - other.StartTicks
                : TimeSpan.TicksPerDay + other.EndTicks - other.StartTicks);
            if (new[] { -week, 0L, week }.Any(shift => from < otherEnd + shift && otherStart + shift < until))
                throw new AvailabilityException("This overlaps another availability window.");
        }
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WeeklyAvailability (GuildId, UserId, Day, StartTicks, EndTicks, TimeZone, Tentative)
            VALUES ({guild}, {user}, {day}, {start}, {end}, {zone}, {window.Tentative})
            ON CONFLICT (GuildId, UserId, Day, StartTicks, TimeZone) DO UPDATE
            SET EndTicks = excluded.EndTicks, Tentative = excluded.Tentative
            """);
        await transaction.CommitAsync();
    }

    public async Task ReplaceWeeklyDayAsync(ulong guildId, ulong userId, DayOfWeek day, string timeZone,
        IReadOnlyList<WeeklyAvailability> ranges)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = guildId.ToString(CultureInfo.InvariantCulture);
        string user = userId.ToString(CultureInfo.InvariantCulture);
        int dayNumber = (int)day;
        await using var transaction = await context.Database.BeginTransactionAsync();
        var others = await WeeklyForMember(context, guildId, userId)
            .Where(row => row.TimeZone == timeZone && row.Day != dayNumber).ToListAsync();
        long week = TimeSpan.TicksPerDay * 7;
        static (long From, long Until) Span(int day, long start, long end)
        {
            long from = day * TimeSpan.TicksPerDay + start;
            return (from, from + (end > start ? end - start : TimeSpan.TicksPerDay + end - start));
        }
        foreach (var range in ranges)
        {
            var (from, until) = Span(dayNumber, range.Start.Ticks, range.End.Ticks);
            foreach (var other in others)
            {
                var (otherFrom, otherUntil) = Span(other.Day, other.StartTicks, other.EndTicks);
                if (new[] { -week, 0L, week }.Any(shift => from < otherUntil + shift && otherFrom + shift < until))
                    throw new AvailabilityException("This overlaps a range on another day.");
            }
        }

        await WeeklyForMember(context, guildId, userId).Where(row => row.Day == dayNumber && row.TimeZone == timeZone).ExecuteDeleteAsync();
        foreach (var range in ranges)
            context.Add(new WeeklyAvailabilityRecord
            {
                GuildId = guild, UserId = user, Day = dayNumber, StartTicks = range.Start.Ticks,
                EndTicks = range.End.Ticks, TimeZone = timeZone, Tentative = range.Tentative
            });
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<bool> RemoveWeeklyAsync(ulong guildId, ulong userId, DayOfWeek day, TimeOnly? start = null, string? timeZone = null)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await WeeklyForMember(context, guildId, userId)
            .Where(row => row.Day == (int)day && (start == null || row.StartTicks == start.Value.Ticks) &&
                (timeZone == null || row.TimeZone == timeZone)).ExecuteDeleteAsync() > 0;
    }

    private static IQueryable<WeeklyAvailabilityRecord> WeeklyForMember(HappyMixDbContext context, ulong guildId, ulong userId)
    {
        string guild = guildId.ToString(CultureInfo.InvariantCulture);
        string user = userId.ToString(CultureInfo.InvariantCulture);
        return context.Set<WeeklyAvailabilityRecord>().Where(row => row.GuildId == guild && row.UserId == user);
    }

    public async Task<IReadOnlyList<AvailabilityWindow>> ListAsync(ulong guildId, ulong userId)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await ForMember(context, guildId, userId).OrderBy(row => row.StartUtc)
            .Select(row => new AvailabilityWindow(row.Id,
                new DateTimeOffset(row.StartUtc, TimeSpan.Zero), new DateTimeOffset(row.EndUtc, TimeSpan.Zero), row.Tentative))
            .ToListAsync();
    }

    public async Task<AvailabilityWindow> SaveAsync(
        ulong guildId, ulong userId, Guid? id, DateTimeOffset start, DateTimeOffset finish, bool tentative = false)
    {
        if (finish <= start)
            throw new AvailabilityException("End must be after start.");

        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var rows = ForMember(context, guildId, userId);
        var existing = id.HasValue ? await rows.SingleOrDefaultAsync(row => row.Id == id.Value) : null;
        if (id.HasValue && existing is null)
            throw new AvailabilityException("Availability window not found.");

        long startUtc = start.UtcTicks;
        long endUtc = finish.UtcTicks;
        if (await rows.AnyAsync(row => (!id.HasValue || row.Id != id.Value) &&
            row.StartUtc < endUtc && startUtc < row.EndUtc))
            throw new AvailabilityException("This overlaps another availability window.");

        var window = existing ?? new AvailabilityRecord
        {
            Id = Guid.NewGuid(),
            GuildId = guildId.ToString(CultureInfo.InvariantCulture),
            UserId = userId.ToString(CultureInfo.InvariantCulture)
        };
        window.StartUtc = startUtc;
        window.EndUtc = endUtc;
        window.Tentative = tentative;
        if (existing is null)
            context.Add(window);

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return new AvailabilityWindow(window.Id, start.ToUniversalTime(), finish.ToUniversalTime(), tentative);
    }

    public async Task<bool> RemoveAsync(ulong guildId, ulong userId, Guid id)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await ForMember(context, guildId, userId).Where(row => row.Id == id).ExecuteDeleteAsync() > 0;
    }

    public async Task<MemberAvailabilitySnapshot> ListForMembersAsync(ulong guildId, IReadOnlyCollection<ulong> userIds,
        DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (userIds.Count == 0)
            return new MemberAvailabilitySnapshot([], []);

        await using var context = await factory.CreateDbContextAsync();
        string guild = guildId.ToString(CultureInfo.InvariantCulture);
        var users = userIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        long from = fromUtc.UtcTicks;
        long to = toUtc.UtcTicks;
        var windows = await context.Set<AvailabilityRecord>()
            .Where(row => row.GuildId == guild && users.Contains(row.UserId) && row.StartUtc < to && from < row.EndUtc)
            .ToListAsync();
        var weekly = await context.Set<WeeklyAvailabilityRecord>()
            .Where(row => row.GuildId == guild && users.Contains(row.UserId))
            .ToListAsync();
        return new MemberAvailabilitySnapshot(
            windows.Select(row => new MemberWindow(ulong.Parse(row.UserId, CultureInfo.InvariantCulture),
                new AvailabilityWindow(row.Id, new DateTimeOffset(row.StartUtc, TimeSpan.Zero), new DateTimeOffset(row.EndUtc, TimeSpan.Zero), row.Tentative)))
                .ToArray(),
            weekly.Select(row => new MemberWeekly(ulong.Parse(row.UserId, CultureInfo.InvariantCulture),
                new WeeklyAvailability((DayOfWeek)row.Day, new TimeOnly(row.StartTicks), new TimeOnly(row.EndTicks), row.TimeZone, row.Tentative)))
                .ToArray());
    }

    private static IQueryable<AvailabilityRecord> ForMember(HappyMixDbContext context, ulong guildId, ulong userId)
    {
        string guild = guildId.ToString(CultureInfo.InvariantCulture);
        string user = userId.ToString(CultureInfo.InvariantCulture);
        return context.Set<AvailabilityRecord>().Where(row => row.GuildId == guild && row.UserId == user);
    }
}

internal sealed class AvailabilityRecord
{
    public Guid Id { get; init; }
    public required string GuildId { get; init; }
    public required string UserId { get; init; }
    public long StartUtc { get; set; }
    public long EndUtc { get; set; }
    public bool Tentative { get; set; }
}

internal sealed class WeeklyAvailabilityRecord
{
    public required string GuildId { get; init; }
    public required string UserId { get; init; }
    public bool Tentative { get; init; }
    public int Day { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
    public required string TimeZone { get; init; }
}
