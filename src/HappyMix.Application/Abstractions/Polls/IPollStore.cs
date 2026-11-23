namespace HappyMix.Application.Abstractions.Polls;

/// <summary>
/// A vote. A group poll is answered by the group's members; a server poll by <see cref="Audience"/>, or by every server
/// member when <see cref="Everyone"/> is set. Member ids are primary accounts of linked account families.
/// </summary>
public sealed record Poll(Guid Id, ulong GuildId, Guid? GroupId, ulong CreatorId, string Question, bool Multiple, bool Everyone,
    DateTimeOffset CreatedAt, DateTimeOffset ClosesAt, DateTimeOffset? ClosedAt, IReadOnlyList<PollOption> Options,
    IReadOnlyList<ulong> Audience, IReadOnlyList<PollVote> Votes)
{
    /// <summary>The Discord channel the vote is posted in, if any.</summary>
    public ulong? ChannelId { get; init; }
    /// <summary>The Discord message showing the vote, once posted.</summary>
    public ulong? MessageId { get; init; }
    /// <summary>Whether the Discord message already shows the final result.</summary>
    public bool Finalized { get; init; }
    /// <summary>
    /// Whether the vote is a Discord poll: people vote with Discord's own poll controls and the bot copies the votes.
    /// Otherwise the bot's message has a menu to vote.
    /// </summary>
    public bool Native { get; init; }
    /// <summary>A normal vote between options, or "who will buy this game" with the fixed <see cref="BuyVote"/> answers.</summary>
    public PollPurpose Purpose { get; init; }
    /// <summary>For a buy vote: the game, as a My games key (<c>steam:730</c> or <c>name:…</c>), so owners can be found.</summary>
    public string? GameKey { get; init; }
    /// <summary>For a buy vote: the game's name.</summary>
    public string? GameName { get; init; }
    public bool IsBuy => Purpose == PollPurpose.Buy;

    public bool IsClosed(DateTimeOffset now) => ClosedAt is not null || now >= ClosesAt;

    /// <summary>When voting ended: closed early, or the planned closing time.</summary>
    public DateTimeOffset EndedAt => ClosedAt is { } closed && closed < ClosesAt ? closed : ClosesAt;

    public int Count(int position) => Votes.Count(vote => vote.Position == position);

    /// <summary>The option with most votes; ties go to the option listed first. Null when nobody voted.</summary>
    public PollOption? Leader => Votes.Count == 0 ? null
        : Options.OrderByDescending(option => Count(option.Position)).ThenBy(option => option.Position).First();
}

public enum PollPurpose { Choice = 0, Buy = 1 }

/// <summary>
/// The four answers of a "will you buy this game" vote, in order. Members who own the game in their imported Steam
/// library are counted as <see cref="Have"/> automatically.
/// </summary>
public static class BuyVote
{
    public const int Ready = 0;
    public const int Willing = 1;
    public const int Have = 2;
    public const int Not = 3;
    public const string Kind = "buy";
    /// <summary>English answers, stored with the vote; pages and Discord show the translated ones by position.</summary>
    public static readonly string[] Labels = ["Ready to buy", "Willing to buy", "Already have", "Not going to buy"];
    public static readonly string[] Emoji = ["🛒", "🤔", "✅", "🚫"];

    /// <summary>The Steam store page when the game is a Steam game.</summary>
    public static string? StoreUrl(string? gameKey) =>
        gameKey is { } key && key.StartsWith("steam:", StringComparison.Ordinal) ? "https://store.steampowered.com/app/" + key[6..] : null;
}

/// <summary>
/// One choice. <see cref="Kind"/> is game, series, film, anime or custom; <see cref="Url"/> is a store, IMDb or anime
/// database link when one was given; <see cref="Media"/> points at its catalog entry, for details such as the poster,
/// year and episodes.
/// </summary>
public sealed record PollOption(int Position, string Label, string Kind, string? Url, HappyMix.Application.Abstractions.Media.MediaRef? Media = null);

public sealed record PollVote(ulong UserId, int Position);

public interface IPollStore
{
    Task<IReadOnlyList<string>> GetTagsAsync(ulong guildId, Guid groupId);

    /// <summary>The tags of every group in the server that has any.</summary>
    Task<IReadOnlyDictionary<Guid, string[]>> ListTagsAsync(ulong guildId);
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

    /// <summary>
    /// Polls whose Discord message needs work: open polls with a channel but no message yet, and ended polls whose
    /// message doesn't show the result yet.
    /// </summary>
    Task<IReadOnlyList<Poll>> ListDiscordWorkAsync(DateTimeOffset now);

    Task SetMessageAsync(Guid pollId, ulong channelId, ulong? messageId, bool finalized, bool native = false);

    /// <summary>The vote shown by a Discord message.</summary>
    Task<Poll?> FindByMessageAsync(ulong messageId);

    /// <summary>Open Discord polls, whose votes are copied from Discord.</summary>
    Task<IReadOnlyList<Poll>> ListOpenNativeAsync(DateTimeOffset now);

    /// <summary>Replaces every vote at once with <paramref name="votes"/> (member id → chosen positions).</summary>
    Task ReplaceVotesAsync(Guid pollId, IReadOnlyDictionary<ulong, int[]> votes);

    /// <summary>Open buy votes of every server, to mark new owners after a Steam import.</summary>
    Task<IReadOnlyList<Poll>> ListOpenBuyAsync(DateTimeOffset now);
}
