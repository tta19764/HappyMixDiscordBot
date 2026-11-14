namespace HappyMix.Application.Abstractions.Guilds;

/// <summary>An alternate Discord account (twink) linked to a primary account within one server.</summary>
public sealed record AccountLink(ulong PrimaryId, ulong AlternateId, DateTimeOffset LinkedAt);

/// <summary>A short-lived, single-use request created by a primary account. Only a hash of its secret is stored.</summary>
public sealed record AccountLinkRequest(Guid Id, ulong PrimaryId, byte[] TokenHash, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? ConsumedAt, ulong? ConsumedBy, bool Revoked)
{
    public bool IsPending(DateTimeOffset now) => ConsumedAt is null && !Revoked && ExpiresAt > now;
}

/// <summary>An account's own availability at the moment it was linked, kept so unlinking can restore it.</summary>
public sealed record AvailabilitySnapshot(AvailabilityWindow[] Windows, WeeklyAvailability[] Weekly)
{
    public static AvailabilitySnapshot Empty { get; } = new([], []);
}

/// <summary>
/// Everything one link changes, applied in a single transaction. Decisions are made by the Application layer;
/// the store only applies them atomically after re-checking that the request is still usable.
/// </summary>
public sealed record LinkPlan(
    Guid RequestId,
    ulong PrimaryId,
    ulong AlternateId,
    DateTimeOffset Now,
    AvailabilitySnapshot AlternateSnapshot,
    IReadOnlyList<Guid> AlternateWindowsToMove,
    IReadOnlyList<Guid> PrimaryWindowsToRemove,
    IReadOnlyList<WeeklyAvailability> PrimaryWeeklyToSet,
    Func<GuildGroup, GuildGroup> UpdateGroup);

public enum LinkOutcome
{
    Linked,
    /// <summary>The same alternate already used this request successfully (a retry).</summary>
    AlreadyLinked,
    /// <summary>Expired, revoked, already used by someone else, or from another server.</summary>
    RequestUnavailable,
    /// <summary>One of the accounts became part of another link meanwhile.</summary>
    AccountUnavailable
}

public interface IAccountLinkStore
{
    Task<IReadOnlyList<AccountLink>> ListAsync(ulong guildId);
    Task<AccountLinkRequest> CreateRequestAsync(ulong guildId, ulong primaryId, byte[] tokenHash, DateTimeOffset now, DateTimeOffset expiresAt);
    Task<AccountLinkRequest?> GetRequestAsync(ulong guildId, Guid requestId);
    Task<IReadOnlyList<AccountLinkRequest>> ListRequestsAsync(ulong guildId, ulong primaryId);
    Task<bool> RevokeRequestAsync(ulong guildId, ulong primaryId, Guid requestId);
    Task<LinkOutcome> LinkAsync(ulong guildId, LinkPlan plan);
    Task<AvailabilitySnapshot?> GetSnapshotAsync(ulong guildId, ulong alternateId);

    /// <summary>Removes the link and restores the alternate's saved availability in one transaction.</summary>
    Task<bool> UnlinkAsync(ulong guildId, ulong primaryId, ulong alternateId, AvailabilitySnapshot restore);
}
