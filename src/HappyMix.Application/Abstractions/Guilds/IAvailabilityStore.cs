namespace HappyMix.Application.Abstractions.Guilds;

/// <summary>A member's one-off availability within a Discord server.</summary>
public sealed record AvailabilityWindow(Guid Id, DateTimeOffset Start, DateTimeOffset End);

public sealed record WeeklyAvailability(DayOfWeek Day, TimeOnly Start, TimeOnly End, string TimeZone);

/// <summary>Stores availability independently for each member and server.</summary>
public interface IAvailabilityStore
{
    Task<IReadOnlyList<AvailabilityWindow>> ListAsync(ulong guildId, ulong userId);

    /// <summary>Creates a window or replaces the specified member-owned window.</summary>
    Task<AvailabilityWindow> SaveAsync(ulong guildId, ulong userId, Guid? id, DateTimeOffset start, DateTimeOffset finish);

    Task<bool> RemoveAsync(ulong guildId, ulong userId, Guid id);
    Task<IReadOnlyList<WeeklyAvailability>> ListWeeklyAsync(ulong guildId, ulong userId);
    Task SaveWeeklyAsync(ulong guildId, ulong userId, WeeklyAvailability window);
    Task<bool> RemoveWeeklyAsync(ulong guildId, ulong userId, DayOfWeek day);
}

public sealed class AvailabilityException(string message) : Exception(message);
