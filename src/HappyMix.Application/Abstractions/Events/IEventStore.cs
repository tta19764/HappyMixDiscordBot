namespace HappyMix.Application.Abstractions.Events;

public enum EventRecurrence { Once = 0, Daily = 1, Weekly = 2 }

public enum OccurrenceStatus { Pending = 0, Confirmed = 1, Canceled = 2 }

public enum EventNotificationKind { Invite = 0, Confirmed = 1, Canceled = 2 }

/// <summary>
/// An event a member organizes. <see cref="StartTime"/> is local to <see cref="TimeZone"/> and is kept on every date,
/// so a weekly 20:00 stays 20:00 across daylight-saving changes. A recurring event always has an <see cref="Until"/> date.
/// </summary>
public sealed record GuildEvent(
    Guid Id, ulong GuildId, ulong OrganizerId, Guid? GroupId, string Title, string? Description, string TimeZone,
    DateOnly StartDate, TimeOnly StartTime, int DurationMinutes, EventRecurrence Recurrence, IReadOnlyList<DayOfWeek> Weekdays,
    DateOnly? Until, DateTimeOffset CreatedAt, DateTimeOffset? CanceledAt);

/// <summary>One date of an event. Responses, confirmation and cancellation are tracked per occurrence.</summary>
public sealed record EventOccurrence(Guid EventId, DateOnly Date, DateTimeOffset Start, DateTimeOffset End,
    OccurrenceStatus Status, DateTimeOffset? ConfirmedAt, DateTimeOffset? CanceledAt);

/// <summary>An invitee's answer for one occurrence. <see cref="Withdrawn"/> marks a decline that replaced an acceptance.</summary>
public sealed record EventResponse(Guid EventId, DateOnly Date, ulong UserId, bool Accepted, bool Withdrawn, DateTimeOffset At);

/// <summary>The delivery state of one Discord message for one person and occurrence.</summary>
public sealed record EventDelivery(Guid EventId, DateOnly Date, ulong UserId, EventNotificationKind Kind, bool Sent, bool Failed, string? Error);

/// <summary>An event with everything the dashboard and the permission checks need.</summary>
public sealed record EventDetails(GuildEvent Event, IReadOnlyList<ulong> Invitees, IReadOnlyList<EventOccurrence> Occurrences,
    IReadOnlyList<EventResponse> Responses, IReadOnlyList<EventDelivery> Deliveries);

/// <summary>A message waiting to be sent, with the event it belongs to.</summary>
public sealed record EventOutboxItem(GuildEvent Event, EventOccurrence Occurrence, ulong UserId, EventNotificationKind Kind, int Attempts);

/// <summary>What changed when someone answered.</summary>
public sealed record EventResponseChange(bool? PreviouslyAccepted, bool Accepted, bool Withdrawn);

public interface IEventStore
{
    /// <summary>Stores a new event. Returns false (and stores nothing) when an event with the same ID already exists.</summary>
    Task<bool> CreateAsync(GuildEvent guildEvent, IReadOnlyCollection<ulong> invitees, IReadOnlyCollection<EventOccurrence> occurrences);

    Task<EventDetails?> GetAsync(ulong guildId, Guid eventId);

    /// <summary>
    /// Events the user organizes or is invited to, with occurrences ending at or after <paramref name="from"/>. Canceled
    /// events and canceled dates are left out.
    /// </summary>
    Task<IReadOnlyList<EventDetails>> ListForUserAsync(ulong guildId, ulong userId, DateTimeOffset from);

    /// <summary>Events of a group, like <see cref="ListForUserAsync"/>: canceled events and dates are left out.</summary>
    Task<IReadOnlyList<EventDetails>> ListForGroupAsync(ulong guildId, Guid groupId, DateTimeOffset from);

    /// <summary>Events of the server with a confirmed date overlapping <paramref name="from"/>–<paramref name="until"/>.</summary>
    Task<IReadOnlyList<EventDetails>> ListConfirmedAsync(ulong guildId, DateTimeOffset from, DateTimeOffset until);

    /// <summary>
    /// Events of the server created, or with a date confirmed or canceled, at or after <paramref name="since"/>.
    /// Occurrences that ended before <paramref name="since"/> are left out.
    /// </summary>
    Task<IReadOnlyList<EventDetails>> ListChangedAsync(ulong guildId, DateTimeOffset since);

    /// <summary>Records an answer. Answering the same way twice changes nothing.</summary>
    Task<EventResponseChange> RespondAsync(Guid eventId, DateOnly day, ulong userId, bool accepted, DateTimeOffset now);

    /// <summary>
    /// Answers every date of a repeating event at once. Accepting joins all dates (clearing earlier per-date declines of
    /// future dates); declining leaves all future dates, so no more invitations are sent. Returns null when the person isn't invited.
    /// </summary>
    Task<EventResponseChange?> RespondSeriesAsync(Guid eventId, ulong userId, bool accepted, DateTimeOffset now);

    /// <summary>
    /// Permanently deletes canceled events (with every date, answer and message) and canceled dates that were canceled
    /// at or before <paramref name="canceledBefore"/> and have no messages still waiting to be sent. Events left without
    /// any date are deleted too. Returns how many events and dates were removed.
    /// </summary>
    Task<int> PurgeCanceledAsync(DateTimeOffset canceledBefore);

    /// <summary>Moves an occurrence from <paramref name="from"/> to <paramref name="target"/>; false when it wasn't in an allowed state.</summary>
    Task<bool> SetStatusAsync(Guid eventId, DateOnly day, IReadOnlyCollection<OccurrenceStatus> from, OccurrenceStatus target, DateTimeOffset now);

    /// <summary>Cancels the series and every occurrence that hasn't started yet. Returns the dates canceled now.</summary>
    Task<IReadOnlyList<DateOnly>> CancelSeriesAsync(Guid eventId, DateTimeOffset now);

    /// <summary>
    /// Hands the event to <paramref name="successor"/> (an invitee), atomically. The previous organizer becomes an invitee who
    /// has accepted every remaining date, so they can withdraw like anyone else. False when <paramref name="current"/> is no
    /// longer the organizer or <paramref name="successor"/> isn't invited.
    /// </summary>
    Task<bool> TransferOrganizerAsync(Guid eventId, ulong current, ulong successor, IReadOnlyCollection<DateOnly> openDates, DateTimeOffset now);

    /// <summary>Queues messages; one already queued for the same person, date and kind is left alone.</summary>
    Task EnqueueAsync(IEnumerable<(Guid EventId, DateOnly Date, ulong UserId, EventNotificationKind Kind)> messages, DateTimeOffset now);

    /// <summary>
    /// Queues invitations for occurrences that start before <paramref name="windowEnd"/>: once per person and date for a
    /// one-off event, and once per person for a repeating event (its next date), unless they already answered.
    /// </summary>
    Task<int> EnqueueDueInvitesAsync(DateTimeOffset now, DateTimeOffset windowEnd);

    Task<IReadOnlyList<EventOutboxItem>> DueMessagesAsync(DateTimeOffset now, int limit);

    /// <summary>Marks a message sent, or failed: retried at <paramref name="retryAt"/>, or never again when it is null.</summary>
    Task MarkAsync(Guid eventId, DateOnly day, ulong userId, EventNotificationKind kind, bool sent, string? failure, DateTimeOffset? retryAt);
}
