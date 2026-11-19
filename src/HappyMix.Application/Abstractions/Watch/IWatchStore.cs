namespace HappyMix.Application.Abstractions.Watch;

public enum WatchStatus { Planned = 0, Watching = 1, Watched = 2, Dropped = 3 }

/// <summary>
/// A series, film or anime on a group's watchlist. For series and anime, <see cref="Season"/> and <see cref="Episode"/>
/// point at the next episode to watch. <see cref="PositionSeconds"/> is where the group stopped inside that episode
/// (or the film), when it wasn't watched to the end. <see cref="Source"/> and <see cref="ExternalId"/> link the item to
/// its catalog entry, when there is one.
/// </summary>
public sealed record WatchItem(
    Guid Id,
    ulong GuildId,
    Guid GroupId,
    string Kind,
    string Title,
    string? Url,
    string? ImageUrl,
    string? Source,
    string? ExternalId,
    WatchStatus Status,
    int Season,
    int Episode,
    int? PositionSeconds,
    ulong AddedBy,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt);

public interface IWatchStore
{
    Task<IReadOnlyList<WatchItem>> ListAsync(ulong guildId, Guid groupId);
    Task<WatchItem?> GetAsync(ulong guildId, Guid itemId);

    /// <summary>Adds the item, or returns the existing one when the group already tracks the same catalog entry.</summary>
    Task<WatchItem> AddAsync(WatchItem item);
    Task SaveAsync(WatchItem item);
    Task<bool> RemoveAsync(ulong guildId, Guid itemId);
}
