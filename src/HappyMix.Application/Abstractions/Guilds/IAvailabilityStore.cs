namespace HappyMix.Application.Abstractions.Guilds;

/// <summary>A member's one-off availability within a Discord server.</summary>
public sealed record AvailabilityWindow(Guid Id, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Stores availability independently for each member and server.</summary>
public interface IAvailabilityStore
{
    Task<IReadOnlyList<AvailabilityWindow>> ListAsync(ulong guildId, ulong userId);

    /// <summary>Creates a window or replaces the specified member-owned window.</summary>
    Task<AvailabilityWindow> SaveAsync(ulong guildId, ulong userId, Guid? id, DateTimeOffset start, DateTimeOffset finish);

    Task<bool> RemoveAsync(ulong guildId, ulong userId, Guid id);
}

public sealed class AvailabilityException(string message) : Exception(message);
