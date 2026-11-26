using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Infrastructure.Caching;

// Caching versions of the stores read on almost every request. Reads go through GuildCache; every write method clears
// the whole server's cache afterwards. Linking accounts also changes groups and availability, so clearing per server
// rather than per table keeps every combination right. A new write method must use cache.WriteAsync too:
// CachedStoreTests fails for a write method that doesn't clear the cache.

internal sealed class CachedAccountLinkStore(IAccountLinkStore inner, GuildCache cache) : IAccountLinkStore
{
    public Task<IReadOnlyList<AccountLink>> ListAsync(ulong guildId) => cache.GetAsync(guildId, "links", _ => inner.ListAsync(guildId));

    public Task<AccountLinkRequest> CreateRequestAsync(ulong guildId, ulong primaryId, byte[] tokenHash, DateTimeOffset now, DateTimeOffset expiresAt) =>
        cache.WriteAsync(guildId, () => inner.CreateRequestAsync(guildId, primaryId, tokenHash, now, expiresAt));

    public Task<AccountLinkRequest?> GetRequestAsync(ulong guildId, Guid requestId) => inner.GetRequestAsync(guildId, requestId);

    public Task<IReadOnlyList<AccountLinkRequest>> ListRequestsAsync(ulong guildId, ulong primaryId) => inner.ListRequestsAsync(guildId, primaryId);

    public Task<bool> RevokeRequestAsync(ulong guildId, ulong primaryId, Guid requestId) =>
        cache.WriteAsync(guildId, () => inner.RevokeRequestAsync(guildId, primaryId, requestId));

    public Task<LinkOutcome> LinkAsync(ulong guildId, LinkPlan plan) => cache.WriteAsync(guildId, () => inner.LinkAsync(guildId, plan));

    public Task<AvailabilitySnapshot?> GetSnapshotAsync(ulong guildId, ulong alternateId) => inner.GetSnapshotAsync(guildId, alternateId);

    public Task<bool> UnlinkAsync(ulong guildId, ulong primaryId, ulong alternateId, AvailabilitySnapshot restore) =>
        cache.WriteAsync(guildId, () => inner.UnlinkAsync(guildId, primaryId, alternateId, restore));
}

internal sealed class CachedGuildGroupStore(IGuildGroupStore inner, GuildCache cache) : IGuildGroupStore
{
    public Task<IReadOnlyList<GuildGroup>> ListAsync(ulong guildId) => cache.GetAsync(guildId, "groups", _ => inner.ListAsync(guildId));

    public Task UpdateAsync(ulong guildId, string name, Func<GuildGroup?, GuildGroup?> update) =>
        cache.WriteAsync(guildId, () => inner.UpdateAsync(guildId, name, update));

    public Task<IReadOnlyList<GroupGame>> ListGamesAsync(ulong guildId) => cache.GetAsync(guildId, "group-games", _ => inner.ListGamesAsync(guildId));

    public Task<bool> SaveGameAsync(ulong guildId, GroupGame game) => cache.WriteAsync(guildId, () => inner.SaveGameAsync(guildId, game));

    public Task<bool> RemoveGameAsync(ulong guildId, Guid groupId, Guid gameId) =>
        cache.WriteAsync(guildId, () => inner.RemoveGameAsync(guildId, groupId, gameId));
}

internal sealed class CachedGuildSettingsStore(IGuildSettingsStore inner, GuildCache cache) : IGuildSettingsStore
{
    public Task<GuildSettings?> GetAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        cache.GetAsync(guildId, "settings", token => inner.GetAsync(guildId, token), cancellationToken);

    public Task SaveAsync(GuildSettings settings, CancellationToken cancellationToken = default) =>
        cache.WriteAsync(settings.GuildId, () => WriteAllAsync(() => inner.SaveAsync(settings, cancellationToken)));

    /// <summary>Every server's settings, for the scheduled jobs; any server's write clears it.</summary>
    public Task<IReadOnlyList<GuildSettings>> ListAsync(CancellationToken cancellationToken) =>
        cache.GetAsync(GuildCache.AllServers, "all-settings", token => inner.ListAsync(token), cancellationToken);

    public Task UpdateAsync(ulong guildId, string defaultTimeZone, Func<GuildSettings, GuildSettings> update, CancellationToken cancellationToken = default) =>
        cache.WriteAsync(guildId, () => WriteAllAsync(() => inner.UpdateAsync(guildId, defaultTimeZone, update, cancellationToken)));

    private Task WriteAllAsync(Func<Task> write) => cache.WriteAsync(GuildCache.AllServers, write);
}
