using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public enum AvailabilitySource
{
    /// <summary>A dated window the member saved for a specific day.</summary>
    OneOff,
    /// <summary>Recurring weekly hours. Certainty is stored separately on each range.</summary>
    Weekly
}

/// <summary>
/// Part of a member's availability that falls on the board date, in the board's time zone.
/// <see cref="FromPreviousDay"/> and <see cref="IntoNextDay"/> mark ranges clipped at local midnight.
/// </summary>
public sealed record BoardRange(AvailabilitySource Source, TimeOnly Start, TimeOnly End, bool FromPreviousDay,
    bool IntoNextDay, bool AllDay, bool Tentative = false);

/// <summary>One saved group member. An empty <see cref="Ranges"/> list means no data, not "unavailable".</summary>
public sealed record BoardRow(ulong UserId, BoardRange[] Ranges)
{
    public bool HasData => Ranges.Length > 0;
}

/// <summary>
/// The board for one date. <see cref="FreeByHour"/> counts, for each local hour 0–23, the members definitely free at any time
/// within that hour. <see cref="Now"/> is the current local time when the date is today.
/// </summary>
public sealed record GroupAvailabilityBoard(Guid GroupId, string TimeZone, DateOnly Today, DateOnly Date,
    DateOnly[] Days, BoardRow[] Rows, int[] FreeByHour, TimeOnly? Now)
{
    public int[] TentativeByHour => Enumerable.Range(0, 24).Select(hour => Rows.Count(row =>
        row.Ranges.Any(range => range.Tentative && StartHours(range) < hour + 1 && EndHours(range) > hour) &&
        !row.Ranges.Any(range => !range.Tentative && StartHours(range) < hour + 1 && EndHours(range) > hour))).ToArray();

    /// <summary>Range end in hours, where a range clipped at midnight ends at 24.</summary>
    public static double EndHours(BoardRange range) =>
        range.AllDay || range.IntoNextDay || (range.End == TimeOnly.MinValue && range.Start != TimeOnly.MinValue)
            ? 24 : range.End.ToTimeSpan().TotalHours;

    public static double StartHours(BoardRange range) => range.AllDay ? 0 : range.Start.ToTimeSpan().TotalHours;
}

/// <summary>
/// Availability of a group's saved members for one of the next seven days. Uses the same roster rule as the
/// group page: members, the organizer and server moderators only. <paramref name="Date"/> outside the window
/// falls back to today.
/// </summary>
public sealed record GetGroupAvailabilityBoardQuery(GroupActor Actor, Guid GroupId, DateOnly? Date, string DefaultTimeZone)
    : IQuery<GroupAvailabilityBoard>;

internal sealed class GroupAvailabilityBoardQueries(IGuildGroupStore groups, IAvailabilityStore availability,
    IGuildSettingsStore settings, IClock clock, IAccountLinkStore accountLinks) : IQueryHandler<GetGroupAvailabilityBoardQuery, GroupAvailabilityBoard>
{
    internal const int Days = 7;

    public async Task<Result<GroupAvailabilityBoard>> Handle(GetGroupAvailabilityBoardQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var links = await accountLinks.LoadAsync(actor.GuildId);
        var group = (await groups.ListAsync(actor.GuildId)).FirstOrDefault(item => item.Id == query.GroupId);
        bool canViewRoster = group is not null && GroupWorkspaceQueries.CanViewRoster(group, actor, links);
        if (!canViewRoster)
            return Result.Failure<GroupAvailabilityBoard>(new("Group.NotFound", GroupWorkspaceQueries.NotFound));

        var saved = await settings.GetAsync(actor.GuildId, cancellationToken);
        var zone = FindZone(saved?.TimeZoneId) ?? FindZone(query.DefaultTimeZone) ?? TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
        var days = Enumerable.Range(0, Days).Select(today.AddDays).ToArray();
        var date = query.Date is { } requested && requested >= today && requested < today.AddDays(Days) ? requested : today;

        var dayStart = LocalTimes.StartOfDay(date, zone);
        var dayEnd = LocalTimes.StartOfDay(date.AddDays(1), zone);
        // One row per member: linked accounts share one identity and one schedule (kept on the primary account).
        var members = links.Representatives(group!.Members);
        var snapshot = await availability.ListForMembersAsync(actor.GuildId, links.Members(members), dayStart, dayEnd);

        var rows = BoardRows.Build(links, members, snapshot, date, zone);

        var freeByHour = Enumerable.Range(0, 24).Select(hour => rows.Count(row => row.Ranges.Any(range =>
            !range.Tentative && GroupAvailabilityBoard.StartHours(range) < hour + 1 && GroupAvailabilityBoard.EndHours(range) > hour))).ToArray();
        TimeOnly? now = date == today ? TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime) : null;
        return Result.Success(new GroupAvailabilityBoard(group.Id, zone.Id, today, date, days, rows, freeByHour, now));
    }

    private static TimeZoneInfo? FindZone(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}

public static class WeeklyOccurrences
{
    /// <summary>
    /// Weekly occurrences that could touch the board date. Each one is resolved in the member's own zone on its
    /// own date, so daylight-saving changes and zone differences across midnight are handled per occurrence.
    /// </summary>
    public static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ForDate(WeeklyAvailability weekly, DateOnly boardDate)
    {
        if (LocalTimes.FindZone(weekly.TimeZone) is not { } zone)
            yield break;

        // Zone offsets differ by at most 26 hours, so occurrences two days either side are enough.
        for (var day = boardDate.AddDays(-2); day <= boardDate.AddDays(2); day = day.AddDays(1))
        {
            if (day.DayOfWeek != weekly.Day)
                continue;

            var start = LocalTimes.ToInstant(day.ToDateTime(weekly.Start), zone);
            var end = weekly.IsFullDay ? LocalTimes.StartOfDay(day.AddDays(1), zone)
                : LocalTimes.ToInstant((weekly.End < weekly.Start ? day.AddDays(1) : day).ToDateTime(weekly.End), zone);
            yield return (start, end);
        }
    }

}
