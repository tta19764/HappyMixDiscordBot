namespace HappyMix.Application.Abstractions.Polls;

/// <summary>
/// A vote. A group poll is answered by the group's members; a server poll by <see cref="Audience"/>, or by every server
/// member when <see cref="Everyone"/> is set. Member ids are primary accounts of linked account families.
/// </summary>
public sealed record Poll(Guid Id, ulong GuildId, Guid? GroupId, ulong CreatorId, string Question, bool Multiple, bool Everyone,
    DateTimeOffset CreatedAt, DateTimeOffset ClosesAt, DateTimeOffset? ClosedAt, IReadOnlyList<PollOption> Options,
    IReadOnlyList<ulong> Audience, IReadOnlyList<PollVote> Votes)
{
    public bool IsClosed(DateTimeOffset now) => ClosedAt is not null || now >= ClosesAt;

    /// <summary>When voting ended: closed early, or the planned closing time.</summary>
    public DateTimeOffset EndedAt => ClosedAt is { } closed && closed < ClosesAt ? closed : ClosesAt;

    public int Count(int position) => Votes.Count(vote => vote.Position == position);

    /// <summary>The option with most votes; ties go to the option listed first. Null when nobody voted.</summary>
    public PollOption? Leader => Votes.Count == 0 ? null
        : Options.OrderByDescending(option => Count(option.Position)).ThenBy(option => option.Position).First();
}

/// <summary>
/// One choice. <see cref="Kind"/> is game, series, film, anime or custom; <see cref="Url"/> is a store, IMDb or anime
/// database link when one was given.
/// </summary>
public sealed record PollOption(int Position, string Label, string Kind, string? Url);

public sealed record PollVote(ulong UserId, int Position);

public interface IPollStore
{
    Task<IReadOnlyList<string>> GetTagsAsync(ulong guildId, Guid groupId);
    Task SetTagsAsync(ulong guildId, Guid groupId, IReadOnlyCollection<string> tags);

    Task CreateAsync(Poll poll);
    Task<Poll?> GetAsync(ulong guildId, Guid pollId);

    /// <summary>
    /// Open polls and up to <paramref name="closedLimit"/> most recently ended ones, of a group, or of the server
    /// itself (polls without a group) when <paramref name="groupId"/> is null.
    /// </summary>
    Task<IReadOnlyList<Poll>> ListAsync(ulong guildId, Guid? groupId, int closedLimit, DateTimeOffset now);

    /// <summary>Replaces the member's choices in the poll. An empty list removes their vote.</summary>
    Task VoteAsync(Guid pollId, ulong userId, IReadOnlyCollection<int> positions);

    /// <summary>Ends voting now. False when it had already ended.</summary>
    Task<bool> CloseAsync(Guid pollId, DateTimeOffset now);
}
