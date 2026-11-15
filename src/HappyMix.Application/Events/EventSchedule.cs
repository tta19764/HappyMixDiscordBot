using HappyMix.Application.Abstractions.Events;

namespace HappyMix.Application.Events;

/// <summary>Turns an event's rule into concrete dates, keeping the local start time on every date.</summary>
public static class EventSchedule
{
    public const int MaxOccurrences = 120;
    public const int MaxSeriesDays = 182;

    /// <summary>The local dates the event happens on, in order.</summary>
    public static IEnumerable<DateOnly> Dates(EventRecurrence recurrence, DateOnly start, DateOnly? until, IReadOnlyCollection<DayOfWeek> weekdays)
    {
        if (recurrence == EventRecurrence.Once)
        {
            yield return start;
            yield break;
        }

        var last = until ?? start;
        for (var date = start; date <= last; date = date.AddDays(1))
        {
            if (recurrence == EventRecurrence.Daily || weekdays.Contains(date.DayOfWeek))
                yield return date;
        }
    }

    /// <summary>
    /// The instant a local date and time happen in <paramref name="zone"/>. A time skipped by a daylight-saving jump
    /// moves to the first valid minute after it; a time that happens twice uses the first of the two.
    /// </summary>
    public static DateTimeOffset Instant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    public static IReadOnlyList<EventOccurrence> Occurrences(GuildEvent guildEvent, TimeZoneInfo zone) =>
        Dates(guildEvent.Recurrence, guildEvent.StartDate, guildEvent.Until, guildEvent.Weekdays).Select(date =>
        {
            var start = Instant(date, guildEvent.StartTime, zone);
            return new EventOccurrence(guildEvent.Id, date, start, start.AddMinutes(guildEvent.DurationMinutes),
                OccurrenceStatus.Pending, null, null);
        }).ToArray();
}
