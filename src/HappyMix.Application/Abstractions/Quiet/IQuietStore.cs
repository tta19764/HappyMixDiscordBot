namespace HappyMix.Application.Abstractions.Quiet;

/// <summary>
/// Weekly do-not-disturb hours in the person's own time zone. Equal start and end means the whole day; an end before
/// the start runs past midnight into the next day.
/// </summary>
public sealed record QuietWeekly(Guid Id, DayOfWeek Day, TimeOnly Start, TimeOnly End, string TimeZone)
{
    public bool IsFullDay => Start == End;
}

/// <summary>Do-not-disturb for a specific stretch of time, such as one date or a holiday.</summary>
public sealed record QuietDates(Guid Id, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// A person's do-not-disturb ranges. They belong to the Discord account, not to a server: personal messages from
/// every server wait while one is on.
/// </summary>
public sealed record QuietSchedule(IReadOnlyList<QuietWeekly> Weekly, IReadOnlyList<QuietDates> Dates)
{
    public static readonly QuietSchedule Empty = new([], []);
    public bool IsEmpty => Weekly.Count == 0 && Dates.Count == 0;
}

/// <summary>
/// A personal message waiting for do-not-disturb to end. <see cref="Kind"/> names the sender that rebuilds it and
/// <see cref="Payload"/> is that sender's JSON; relevance is checked again before it goes out.
/// </summary>
public sealed record HeldMessage(long Id, ulong UserId, string Kind, string? Key, string Payload, DateTimeOffset HeldAt, DateTimeOffset ReleaseAt);

public interface IQuietStore
{
    Task<QuietSchedule> GetAsync(ulong userId);
    Task<IReadOnlyDictionary<ulong, QuietSchedule>> GetManyAsync(IReadOnlyCollection<ulong> userIds);
    Task AddWeeklyAsync(ulong userId, IReadOnlyCollection<QuietWeekly> ranges);
    /// <summary>Replaces every weekly do-not-disturb range on <paramref name="day"/> with <paramref name="ranges"/>.</summary>
    Task ReplaceWeeklyDayAsync(ulong userId, DayOfWeek day, IReadOnlyCollection<QuietWeekly> ranges);
    Task AddDatesAsync(ulong userId, IReadOnlyCollection<QuietDates> ranges);
    Task<bool> RemoveAsync(ulong userId, Guid id);
    /// <summary>Removes date ranges that ended before <paramref name="before"/>.</summary>
    Task PruneDatesAsync(DateTimeOffset before);

    /// <summary>
    /// Queues a message. A <paramref name="key"/> collapses repeats: a newer message with the same person, kind and key
    /// replaces the waiting one, so someone coming back from do-not-disturb gets one message, not a pile.
    /// </summary>
    Task HoldAsync(ulong userId, string kind, string? key, string payload, DateTimeOffset heldAt, DateTimeOffset releaseAt);
    Task<IReadOnlyList<HeldMessage>> DueAsync(DateTimeOffset now, int limit);
    Task<int> CountHeldAsync(ulong userId);
    Task RescheduleAsync(long id, DateTimeOffset releaseAt);
    Task RemoveHeldAsync(long id);
}

/// <summary>Sends one kind of held personal message once do-not-disturb is over and the message still matters.</summary>
public interface IHeldMessageSender
{
    string Kind { get; }
    Task<HeldDelivery> SendAsync(HeldMessage message, CancellationToken cancellationToken);
}

public enum HeldDelivery { Sent, Dropped, RetryLater }
