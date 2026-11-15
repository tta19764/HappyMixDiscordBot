namespace HappyMix.Application.Abstractions.Guilds;

/// <summary>A member's one-off availability within a Discord server.</summary>
public sealed record AvailabilityWindow(Guid Id, DateTimeOffset Start, DateTimeOffset End, bool Tentative = false);

/// <summary>
/// A recurring weekly window in its own time zone. Equal start and end (00:00–00:00) means the whole day,
/// anchored to local midnight so it stays a full day across daylight-saving changes.
/// </summary>
public sealed record WeeklyAvailability(DayOfWeek Day, TimeOnly Start, TimeOnly End, string TimeZone, bool Tentative = false)
{
    public bool IsFullDay => Start == End;
}

public sealed record MemberWindow(ulong UserId, AvailabilityWindow Window);
public sealed record MemberWeekly(ulong UserId, WeeklyAvailability Weekly);

/// <summary>Availability for several members, read in one pass.</summary>
public sealed record MemberAvailabilitySnapshot(IReadOnlyList<MemberWindow> Windows, IReadOnlyList<MemberWeekly> Weekly);

/// <summary>Stores availability independently for each member and server.</summary>
public interface IAvailabilityStore
{
    Task<IReadOnlyList<AvailabilityWindow>> ListAsync(ulong guildId, ulong userId);

    /// <summary>Creates a window or replaces the specified member-owned window.</summary>
    Task<AvailabilityWindow> SaveAsync(ulong guildId, ulong userId, Guid? id, DateTimeOffset start, DateTimeOffset finish, bool tentative = false);

    Task SaveDatesAsync(ulong guildId, ulong userId, IReadOnlyList<AvailabilityWindow> windows);

    Task<bool> RemoveAsync(ulong guildId, ulong userId, Guid id);
    Task<IReadOnlyList<WeeklyAvailability>> ListWeeklyAsync(ulong guildId, ulong userId);
    /// <summary>Adds a weekly range or updates the range with the same day, start and time zone.</summary>
    Task SaveWeeklyAsync(ulong guildId, ulong userId, WeeklyAvailability window);
    /// <summary>
    /// Replaces the ranges on one day in one time zone. Rejects overlaps with the member's other days in that zone,
    /// including overnight ranges spilling into the next or previous day.
    /// </summary>
    Task ReplaceWeeklyDayAsync(ulong guildId, ulong userId, DayOfWeek day, string timeZone, IReadOnlyList<WeeklyAvailability> ranges);
    /// <summary>Removes all ranges on a day, or just the range identified by start and time zone.</summary>
    Task<bool> RemoveWeeklyAsync(ulong guildId, ulong userId, DayOfWeek day, TimeOnly? start = null, string? timeZone = null);

    /// <summary>One-off windows overlapping the UTC range, plus weekly windows, for the given members only.</summary>
    Task<MemberAvailabilitySnapshot> ListForMembersAsync(ulong guildId, IReadOnlyCollection<ulong> userIds,
        DateTimeOffset fromUtc, DateTimeOffset toUtc);
}

public sealed class AvailabilityException(string message) : Exception(message);
