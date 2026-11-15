using System.Globalization;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Events;
using HappyMix.Bot.Services;
using Microsoft.Extensions.Localization;

namespace HappyMix.Bot.Web;

/// <summary>One date of an event as a dashboard row, from the viewer's point of view.</summary>
public sealed record EventRow(Guid EventId, DateOnly Date, string Title, string When, string Zone, OccurrenceStatus Status,
    string? GroupName, EventCounts Counts, bool Repeats, bool? MyAnswer, bool Withdrawn, bool IsOrganizer, bool IsInvitee,
    string Organizer, string Url, string DayShort, string DayNumber, bool DmFailed)
{
    public string StatusKey => Status switch { OccurrenceStatus.Confirmed => "confirmed", OccurrenceStatus.Canceled => "canceled", _ => "pending" };
}

public sealed record EventsPage(ulong GuildId, string GuildName, bool CanConfigure, IReadOnlyList<EventRow> Invitations,
    IReadOnlyList<EventRow> Accepted, IReadOnlyList<EventRow> Organizing, BannerModel? Banner);

public sealed record EventPerson(ulong Id, string Name, MemberProfile Profile);

public sealed class EventForm
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public string Audience { get; set; } = "group";
    public Guid? GroupId { get; set; }
    public ulong[] Invitees { get; set; } = [];
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string StartDate { get; set; } = "";
    public string StartHour { get; set; } = "20";
    public string StartMinute { get; set; } = "00";
    public string EndHour { get; set; } = "23";
    public string EndMinute { get; set; } = "00";
    public string? TimeZone { get; set; }
    public string Repeat { get; set; } = "once";
    public DayOfWeek[] Weekdays { get; set; } = [];
    public string? Until { get; set; }
}

public sealed record EventFormPage(ulong GuildId, string GuildName, bool CanConfigure, EventForm Form,
    IReadOnlyList<EventGroupOption> Groups, IReadOnlyList<EventPerson> People, string ServerZone, string? Error);

public sealed record EventResponseRow(EventPerson Person, string State, bool Withdrawn, bool DmFailed, DateTimeOffset? At);

public sealed record EventDetailPage(ulong GuildId, string GuildName, bool CanConfigure, EventView View, EventOccurrence Occurrence,
    string Title, string When, string Schedule, string Organizer, string? GroupName, Guid? GroupId, EventCounts Counts,
    IReadOnlyList<EventResponseRow> Responses, IReadOnlyList<EventRow> Dates, bool? MyAnswer, bool Withdrawn, bool Started,
    IReadOnlyList<string> RecentWithdrawals, int FailedDms, BannerModel? Banner)
{
    public bool Repeats => View.Details.Event.Recurrence != EventRecurrence.Once;
    public bool SeriesCanceled => View.Details.Event.CanceledAt is not null;
    public bool CanChange => View.IsOrganizer && !Started && Occurrence.Status != OccurrenceStatus.Canceled;
}

/// <summary>
/// A confirmed event drawn on a member's row of an availability board, in hours 0–24 of the board date. A null
/// <see cref="Url"/> means the viewer isn't part of the event, so the title is replaced by a generic label.
/// </summary>
public sealed record BoardEvent(ulong UserId, string Title, double StartHour, double EndHour, string Time, string? Url)
{
    /// <summary>Under 90 minutes: too narrow for a label inside the block.</summary>
    public bool IsShort => EndHour - StartHour < 1.5;
}

/// <summary>Events for one board row, placed on the visible hour range.</summary>
public sealed record BoardEventsModel(IEnumerable<BoardEvent> Events, int FirstHour = 0, int HourSpan = 24);

public static class BoardEvents
{
    public static ILookup<ulong, BoardEvent> Empty { get; } = Array.Empty<BoardEvent>().ToLookup(item => item.UserId);

    /// <summary>Splits event dates at local midnight and keeps the part on <paramref name="date"/>.</summary>
    public static ILookup<ulong, BoardEvent> ForDate(IEnumerable<MemberEventBlock> blocks, DateOnly date, TimeZoneInfo zone, ulong guildId, string hiddenTitle)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        return blocks.Select(block =>
        {
            var start = TimeZoneInfo.ConvertTime(block.Start, zone).DateTime;
            var end = TimeZoneInfo.ConvertTime(block.End, zone).DateTime;
            double from = Math.Max(0, (start - dayStart).TotalHours), until = Math.Min(24, (end - dayStart).TotalHours);
            return until <= from ? null : new BoardEvent(block.UserId, block.Visible ? block.Title : hiddenTitle, from, until,
                $"{start:HH:mm}–{end:HH:mm}", block.Visible ? $"/events/{guildId}/{block.EventId}?date={block.Date:yyyy-MM-dd}" : null);
        }).OfType<BoardEvent>().ToLookup(item => item.UserId);
    }
}

/// <summary>Formats event times and schedules in the event's own time zone and the viewer's language.</summary>
public static class EventText
{
    public static string When(EventOccurrence occurrence, TimeZoneInfo zone)
    {
        var culture = CultureInfo.CurrentUICulture;
        var start = TimeZoneInfo.ConvertTime(occurrence.Start, zone);
        var end = TimeZoneInfo.ConvertTime(occurrence.End, zone);
        string day = start.ToString("ddd d MMM", culture);
        string next = end.Date > start.Date ? $"{end.ToString("ddd", culture)} " : "";
        return $"{day} · {start:HH:mm}–{next}{end:HH:mm}";
    }

    /// <summary>"Every Monday, Wednesday and Friday, 20:00–23:00 · until 19 Dec".</summary>
    public static string Schedule(GuildEvent guildEvent, IStringLocalizer text)
    {
        var culture = CultureInfo.CurrentUICulture;
        var end = guildEvent.StartTime.AddMinutes(guildEvent.DurationMinutes);
        string times = $"{guildEvent.StartTime:HH\\:mm}–{end:HH\\:mm}";
        string Day(DayOfWeek day) => culture.DateTimeFormat.GetDayName(day);
        string until = guildEvent.Until is { } last ? last.ToString("d MMM yyyy", culture) : "";
        return guildEvent.Recurrence switch
        {
            EventRecurrence.Daily => text["Every_day_Arg0_until_Arg1", times, until],
            EventRecurrence.Weekly => text["Every_Arg0_Arg1_until_Arg2", Join(guildEvent.Weekdays.OrderBy(day => ((int)day + 6) % 7).Select(Day).ToArray(), text), times, until],
            _ => text["Once_Arg0_Arg1", guildEvent.StartDate.ToString("dddd d MMMM yyyy", culture), times]
        };
    }

    public static string Join(IReadOnlyList<string> items, IStringLocalizer text) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => text["Arg0_and_Arg1", string.Join(", ", items.Take(items.Count - 1)), items[^1]]
    };

    public static EventRow Row(EventDetails details, EventOccurrence occurrence, ulong viewer, TimeZoneInfo zone, string? groupName,
        Func<ulong, string> name, ulong guildId)
    {
        var culture = CultureInfo.CurrentUICulture;
        var mine = details.Responses.FirstOrDefault(item => item.UserId == viewer && item.Date == occurrence.Date);
        var start = TimeZoneInfo.ConvertTime(occurrence.Start, zone);
        bool dmFailed = details.Deliveries.Any(item => item.UserId == viewer && item.Date == occurrence.Date && item.Kind == EventNotificationKind.Invite && item.Failed);
        return new EventRow(details.Event.Id, occurrence.Date, details.Event.Title, When(occurrence, zone), zone.Id, occurrence.Status, groupName,
            EventRules.Count(details, occurrence.Date), details.Event.Recurrence != EventRecurrence.Once, mine?.Accepted, mine?.Withdrawn == true,
            details.Event.OrganizerId == viewer, details.Invitees.Contains(viewer), name(details.Event.OrganizerId),
            $"/events/{guildId}/{details.Event.Id}?date={occurrence.Date:yyyy-MM-dd}", start.ToString("ddd", culture), start.Day.ToString(culture), dmFailed);
    }

    public static TimeZoneInfo Zone(GuildEvent guildEvent) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(guildEvent.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
}
