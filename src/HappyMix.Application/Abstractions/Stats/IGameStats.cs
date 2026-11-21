namespace HappyMix.Application.Abstractions.Stats;

/// <summary>Games with stats cards. A group gets a card game when its games include that Steam app.</summary>
public enum StatsGame { Dota2, Cs2 }

/// <summary>Why a card has no numbers: shown to the owner as a fix, to others as a plain state.</summary>
public enum StatsStatus
{
    Ok,
    /// <summary>The account has no public match data (Dota 2: "Expose Public Match Data" is off).</summary>
    NoPublicData,
    /// <summary>No account on the stats service (CS2: no FACEIT account for this Steam account).</summary>
    NoAccount,
    /// <summary>The stats service didn't answer; the last good data, if any, is kept.</summary>
    Error
}

public sealed record DotaHero(int Id, string Name, string? Image, int Games, int Wins);

/// <param name="RankTier">Dota's medal as two digits: tens are the medal (1 Herald … 8 Immortal), ones the stars.</param>
/// <param name="Wins">Wins over the last 30 days.</param>
/// <param name="TopHeroes">Most played heroes over the last 90 days.</param>
public sealed record DotaProfile(long AccountId, string? Persona, int? RankTier, int? LeaderboardRank, int Wins, int Losses,
    IReadOnlyList<DotaHero> TopHeroes);

public sealed record Cs2Map(string Name, int Matches, double? WinRate);

/// <param name="Level">FACEIT skill level, 1–10.</param>
public sealed record Cs2Profile(string FaceitId, string Nickname, int? Level, int? Elo, int Matches, double? WinRate, double? KillDeath,
    double? Headshots, IReadOnlyList<Cs2Map> Maps);

/// <summary>A fetched card for one person and game. <see cref="Payload"/> is the profile as JSON when there is one.</summary>
public sealed record CachedStats(ulong UserId, StatsGame Game, StatsStatus Status, string? Payload, DateTimeOffset FetchedAt);

public sealed record StatsFetch<T>(StatsStatus Status, T? Profile) where T : class;

/// <summary>
/// One person's line in a match: the result from their side, their hero (Dota 2) or the map (CS2) and their numbers.
/// <see cref="MatchId"/> is the service's id (OpenDota match id, FACEIT match id), the same for everyone in the match.
/// </summary>
/// <param name="Pick">Dota 2: the hero's name. CS2: the map, like de_mirage.</param>
/// <param name="Image">Dota 2: the hero picture.</param>
/// <param name="Score">CS2: the final score from this player's side, like "13 / 9".</param>
public sealed record PlayerMatch(ulong UserId, StatsGame Game, string MatchId, DateTimeOffset StartedAt, int DurationSeconds, bool Won,
    string? Pick, string? Image, int Kills, int Deaths, int Assists, double? Headshots = null, string? Score = null);

/// <summary>A match two or more members of a group played together.</summary>
public sealed record GroupRecap(Guid GroupId, StatsGame Game, string MatchId, DateTimeOffset StartedAt, int DurationSeconds,
    IReadOnlyList<PlayerMatch> Players)
{
    /// <summary>Won, lost, or null when the members played on opposite sides.</summary>
    public bool? Won => Players.All(player => player.Won) ? true : Players.All(player => !player.Won) ? false : null;
}

/// <summary>Stats sharing choices and the card cache.</summary>
public interface IGameStatsStore
{
    Task<IReadOnlySet<ulong>> ListSharingAsync(IReadOnlyCollection<ulong> userIds);
    Task<IReadOnlyList<ulong>> ListAllSharingAsync();
    Task SetSharingAsync(ulong userId, bool share, DateTimeOffset at);
    Task<IReadOnlyList<CachedStats>> ListCachedAsync(IReadOnlyCollection<ulong> userIds);
    Task SaveCachedAsync(CachedStats stats);
    Task RemoveCachedAsync(ulong userId);

    /// <summary>Adds matches; a match already saved for that person and game is left as it is.</summary>
    Task SaveMatchesAsync(IReadOnlyCollection<PlayerMatch> matches);
    Task<IReadOnlyList<PlayerMatch>> ListMatchesAsync(IReadOnlyCollection<ulong> userIds, DateTimeOffset since);
    Task<DateTimeOffset?> MatchesFetchedAtAsync(ulong userId, StatsGame game);
    Task SetMatchesFetchedAtAsync(ulong userId, StatsGame game, DateTimeOffset at);
    Task RemoveMatchesAsync(ulong userId);
    Task PruneMatchesAsync(DateTimeOffset before);

    /// <summary>Records that a group's recap of a match was handled; false when it already was.</summary>
    Task<bool> MarkRecapAsync(ulong guildId, Guid groupId, StatsGame game, string matchId, DateTimeOffset at);
    Task<bool> GetRecapChannelAsync(ulong guildId, Guid groupId);
    Task SetRecapChannelAsync(ulong guildId, Guid groupId, bool post);
}

/// <summary>Posts a group's match recap in its notification channel. Best effort.</summary>
public interface IRecapPublisher
{
    Task PublishAsync(ulong guildId, string groupName, GroupRecap recap, CancellationToken cancellationToken);
}

/// <summary>Public stats services: OpenDota for Dota 2 and FACEIT for CS2.</summary>
public interface IGameStatsDirectory
{
    /// <summary>Whether CS2 cards can be fetched (a FACEIT key is configured).</summary>
    bool Cs2Enabled { get; }
    Task<StatsFetch<DotaProfile>> GetDotaAsync(string steamId64, CancellationToken cancellationToken);
    Task<StatsFetch<Cs2Profile>> GetCs2Async(string steamId64, CancellationToken cancellationToken);

    /// <summary>The person's last matches (OpenDota: up to 20; FACEIT: up to 10, with their stats per match).</summary>
    /// <param name="known">Match ids already saved, so the service isn't asked for their details again.</param>
    Task<StatsFetch<IReadOnlyList<PlayerMatch>>> GetMatchesAsync(ulong userId, StatsGame game, string steamId64,
        IReadOnlySet<string> known, CancellationToken cancellationToken);
}
