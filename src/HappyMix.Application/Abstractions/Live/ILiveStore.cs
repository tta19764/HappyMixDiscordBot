namespace HappyMix.Application.Abstractions.Live;

/// <summary>
/// A member's Live choices for one group, both off by default: <see cref="Share"/> lets the group see what they play;
/// <see cref="Notify"/> sends them a DM when a member who shares starts a game the group cares about.
/// </summary>
public sealed record LiveMembership(ulong GuildId, Guid GroupId, ulong UserId, bool Share, bool Notify);

/// <summary>A stretch of playing one game, seen through Discord's status or Steam. Open while <see cref="EndedAt"/> is null.</summary>
public sealed record PlaySession(long Id, ulong UserId, string Game, string Source, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

public interface ILiveStore
{
    Task<IReadOnlyList<LiveMembership>> ListMembershipsAsync(ulong guildId, Guid groupId);

    /// <summary>Every group, in any server, where the person shares what they play.</summary>
    Task<IReadOnlyList<LiveMembership>> ListSharingAsync(ulong userId);

    /// <summary>Everyone who shares with at least one group.</summary>
    Task<IReadOnlyList<ulong>> ListSharersAsync();

    Task SaveMembershipAsync(LiveMembership membership);
    Task<bool> GetChannelPostsAsync(ulong guildId, Guid groupId);
    Task SetChannelPostsAsync(ulong guildId, Guid groupId, bool post);

    /// <summary>Sessions of these people still open or ended after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<PlaySession>> ListSessionsAsync(IReadOnlyCollection<ulong> userIds, DateTimeOffset since);
    Task<PlaySession?> GetOpenAsync(ulong userId, string source);
    Task<long> StartAsync(ulong userId, string game, string source, DateTimeOffset at);
    Task EndAsync(long sessionId, DateTimeOffset at);

    /// <summary>When the group last heard about this player starting this game, for the cooldown.</summary>
    Task<DateTimeOffset?> LastAlertAsync(ulong guildId, Guid groupId, ulong userId, string game);
    Task SaveAlertAsync(ulong guildId, Guid groupId, ulong userId, string game, DateTimeOffset at);
    Task PruneAsync(DateTimeOffset before);
}

/// <summary>Someone in a group started a game the group cares about.</summary>
/// <param name="AlsoWant">Other members who marked the game "Want to play".</param>
/// <param name="DmRecipients">Members who asked for DMs from this group (never the player).</param>
public sealed record LiveNotice(ulong GuildId, Guid GroupId, string GroupName, ulong PlayerId, string Game, IReadOnlyList<ulong> AlsoWant,
    IReadOnlyList<ulong> DmRecipients, bool PostInChannel);

/// <summary>Delivers Live notices (DMs and the group channel). Best effort: delivery problems never fail detection.</summary>
public interface ILiveNotifier
{
    Task NotifyAsync(LiveNotice notice, CancellationToken cancellationToken);
}
