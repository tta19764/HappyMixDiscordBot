using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>
/// Availability of a set of members on one date, plus how many of them have time on each day of that date's month.
/// Used by the server-wide "Members" tab and by the new-event form.
/// </summary>
public sealed record MembersAvailability(string TimeZone, DateOnly Today, DateOnly Date, BoardRow[] Rows, int[] FreeByHour,
    IReadOnlyDictionary<DateOnly, int> MonthCounts);

/// <summary>
/// <paramref name="Members"/> are Discord accounts; linked accounts collapse into one row. <paramref name="Date"/> defaults
/// to today in the server time zone. <paramref name="IncludeMonth"/> also counts members with time on each day of the month.
/// </summary>
public sealed record GetMembersAvailabilityQuery(ulong GuildId, IReadOnlyCollection<ulong> Members, DateOnly? Date,
    string DefaultTimeZone, bool IncludeMonth = false) : IQuery<MembersAvailability>;

internal sealed class MembersAvailabilityQueries(IAvailabilityStore availability, IGuildSettingsStore settings, IClock clock,
    IAccountLinkStore accountLinks) : IQueryHandler<GetMembersAvailabilityQuery, MembersAvailability>
{
    internal const int MaxMembers = 200;

    public async Task<Result<MembersAvailability>> Handle(GetMembersAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var links = await accountLinks.LoadAsync(query.GuildId);
        var saved = await settings.GetAsync(query.GuildId, cancellationToken);
        var zone = Find(saved?.TimeZoneId) ?? Find(query.DefaultTimeZone) ?? TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
        var date = query.Date ?? today;
        var members = links.Representatives(query.Members).Take(MaxMembers).ToArray();

        var first = query.IncludeMonth ? new DateOnly(date.Year, date.Month, 1) : date;
        var last = query.IncludeMonth ? first.AddMonths(1).AddDays(-1) : date;
        var snapshot = await availability.ListForMembersAsync(query.GuildId, links.Members(members),
            LocalTimes.StartOfDay(first, zone), LocalTimes.StartOfDay(last.AddDays(1), zone));

        var rows = BoardRows.Build(links, members, snapshot, date, zone);
        var freeByHour = Enumerable.Range(0, 24).Select(hour => rows.Count(row => row.Ranges.Any(range =>
            !range.Tentative && GroupAvailabilityBoard.StartHours(range) < hour + 1 && GroupAvailabilityBoard.EndHours(range) > hour))).ToArray();
        var month = new Dictionary<DateOnly, int>();
        if (query.IncludeMonth)
            for (var day = first; day <= last; day = day.AddDays(1))
                month[day] = day == date ? rows.Count(row => row.HasData) : BoardRows.Build(links, members, snapshot, day, zone).Count(row => row.HasData);
        return Result.Success(new MembersAvailability(zone.Id, today, date, rows, freeByHour, month));
    }

    private static TimeZoneInfo? Find(string? id) => id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}

/// <summary>Turns stored windows and weekly hours into one board row per member for a local date.</summary>
public static class BoardRows
{
    public static BoardRow[] Build(MemberLinks links, IReadOnlyList<ulong> members, MemberAvailabilitySnapshot snapshot, DateOnly date, TimeZoneInfo zone)
    {
        var dayStart = LocalTimes.StartOfDay(date, zone);
        var dayEnd = LocalTimes.StartOfDay(date.AddDays(1), zone);
        var windows = snapshot.Windows.ToLookup(item => item.UserId, item => item.Window);
        var weekly = snapshot.Weekly.ToLookup(item => item.UserId, item => item.Weekly);
        return members.Select(userId => new BoardRow(userId,
                windows[links.Member(userId)].Select(window => (AvailabilitySource.OneOff, window.Start, window.End, window.Tentative))
                    .Concat(weekly[links.Member(userId)].SelectMany(item => WeeklyOccurrences.ForDate(item, date)
                        .Select(occurrence => (AvailabilitySource.Weekly, occurrence.Start, occurrence.End, item.Tentative))))
                    .Where(item => item.Item2 < dayEnd && dayStart < item.Item3)
                    .OrderBy(item => item.Item2).ThenBy(item => item.Item1)
                    .Select(item => Clip(item.Item1, item.Item2, item.Item3, dayStart, dayEnd, zone, item.Item4))
                    .ToArray()))
            .OrderByDescending(row => row.HasData)
            .ToArray();
    }

    private static BoardRange Clip(AvailabilitySource source, DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset dayStart, DateTimeOffset dayEnd, TimeZoneInfo zone, bool tentative)
    {
        bool fromPrevious = start < dayStart;
        bool intoNext = end > dayEnd;
        var from = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromPrevious ? dayStart : start, zone).DateTime);
        var until = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(intoNext ? dayEnd : end, zone).DateTime);
        return new BoardRange(source, from, until, fromPrevious, intoNext, start <= dayStart && end >= dayEnd, tentative);
    }
}
