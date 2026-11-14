using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public enum AvailabilitySource
{
    /// <summary>A dated window the member saved for a specific day.</summary>
    OneOff,
    /// <summary>Recurring weekly hours. Expected, not confirmed for the date.</summary>
    Weekly
}

/// <summary>
/// Part of a member's availability that falls on the board date, in the board's time zone.
/// <see cref="FromPreviousDay"/> and <see cref="IntoNextDay"/> mark ranges clipped at local midnight.
/// </summary>
public sealed record BoardRange(AvailabilitySource Source, TimeOnly Start, TimeOnly End, bool FromPreviousDay,
    bool IntoNextDay, bool AllDay);

/// <summary>One saved group member. An empty <see cref="Ranges"/> list means no data, not "unavailable".</summary>
public sealed record BoardRow(ulong UserId, BoardRange[] Ranges)
{
    public bool HasData => Ranges.Length > 0;
}

public sealed record GroupAvailabilityBoard(Guid GroupId, string TimeZone, DateOnly Today, DateOnly Date,
    DateOnly[] Days, BoardRow[] Rows);

/// <summary>
/// Availability of a group's saved members for one of the next seven days. Uses the same roster rule as the
/// group page: members, the organizer and server moderators only. <paramref name="Date"/> outside the window
/// falls back to today.
/// </summary>
public sealed record GetGroupAvailabilityBoardQuery(GroupActor Actor, Guid GroupId, DateOnly? Date, string DefaultTimeZone)
    : IQuery<GroupAvailabilityBoard>;

internal sealed class GroupAvailabilityBoardQueries(IGuildGroupStore groups, IAvailabilityStore availability,
    IGuildSettingsStore settings, IClock clock) : IQueryHandler<GetGroupAvailabilityBoardQuery, GroupAvailabilityBoard>
{
    internal const int Days = 7;

    public async Task<Result<GroupAvailabilityBoard>> Handle(GetGroupAvailabilityBoardQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var group = (await groups.ListAsync(actor.GuildId)).FirstOrDefault(item => item.Id == query.GroupId);
        bool canViewRoster = group is not null && GroupWorkspaceQueries.CanSee(group, actor) &&
            (actor.CanModerate || group.OrganizerId == actor.UserId || group.Members.Contains(actor.UserId));
        if (!canViewRoster)
            return Result.Failure<GroupAvailabilityBoard>(new("Group.NotFound", GroupWorkspaceQueries.NotFound));

        var saved = await settings.GetAsync(actor.GuildId, cancellationToken);
        var zone = FindZone(saved?.TimeZoneId) ?? FindZone(query.DefaultTimeZone) ?? TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
        var days = Enumerable.Range(0, Days).Select(today.AddDays).ToArray();
        var date = query.Date is { } requested && requested >= today && requested < today.AddDays(Days) ? requested : today;

        var dayStart = LocalTimes.StartOfDay(date, zone);
        var dayEnd = LocalTimes.StartOfDay(date.AddDays(1), zone);
        var members = group!.Members.Distinct().ToArray();
        var snapshot = await availability.ListForMembersAsync(actor.GuildId, members, dayStart, dayEnd);

        var windows = snapshot.Windows.ToLookup(item => item.UserId, item => item.Window);
        var weekly = snapshot.Weekly.ToLookup(item => item.UserId, item => item.Weekly);
        var rows = members.Select(userId => new BoardRow(userId,
                windows[userId].Select(window => (AvailabilitySource.OneOff, window.Start, window.End))
                    .Concat(weekly[userId].SelectMany(item => Occurrences(item, date))
                        .Select(occurrence => (AvailabilitySource.Weekly, occurrence.Start, occurrence.End)))
                    .Where(item => item.Item2 < dayEnd && dayStart < item.Item3)
                    .OrderBy(item => item.Item2).ThenBy(item => item.Item1)
                    .Select(item => Clip(item.Item1, item.Item2, item.Item3, dayStart, dayEnd, zone))
                    .ToArray()))
            .OrderByDescending(row => row.HasData)
            .ToArray();

        return Result.Success(new GroupAvailabilityBoard(group.Id, zone.Id, today, date, days, rows));
    }

    /// <summary>
    /// Weekly occurrences that could touch the board date. Each one is resolved in the member's own zone on its
    /// own date, so daylight-saving changes and zone differences across midnight are handled per occurrence.
    /// </summary>
    internal static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Occurrences(WeeklyAvailability weekly, DateOnly boardDate)
    {
        var zone = FindZone(weekly.TimeZone);
        if (zone is null)
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

    private static BoardRange Clip(AvailabilitySource source, DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset dayStart, DateTimeOffset dayEnd, TimeZoneInfo zone)
    {
        bool fromPrevious = start < dayStart;
        bool intoNext = end > dayEnd;
        var from = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromPrevious ? dayStart : start, zone).DateTime);
        var until = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(intoNext ? dayEnd : end, zone).DateTime);
        return new BoardRange(source, from, until, fromPrevious, intoNext, start <= dayStart && end >= dayEnd);
    }

    private static TimeZoneInfo? FindZone(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}
