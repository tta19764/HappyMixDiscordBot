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
    IReadOnlyList<DotaHero> TopHeroes)
{
    /// <summary>The last (up to) 20 matches: form, averages, solo vs party and ranked results.</summary>
    public DotaRecent? Recent { get; init; }
    /// <summary>Games per role, most played first: positions 1–5 from STRATZ, or lanes from OpenDota.</summary>
    public IReadOnlyList<DotaRole> Roles { get; init; } = [];
    /// <summary>"STRATZ" (positions over the last 50 matches) or "OpenDota" (lanes over the last 20).</summary>
    public string? RolesFrom { get; init; }
    /// <summary>People played with on the same team (OpenDota peers), most games first.</summary>
    public IReadOnlyList<DotaPeer> Peers { get; init; } = [];
    /// <summary>The medal once a day, kept by the bot: OpenDota only knows the current one. Oldest first.</summary>
    public IReadOnlyList<DotaRankPoint> RankHistory { get; init; } = [];
}

/// <param name="Form">Newest first: W for a win, L for a loss.</param>
/// <param name="Streak">Positive: wins in a row; negative: losses in a row.</param>
/// <param name="RankedGames">Ranked matchmaking games among them (the ones that move MMR).</param>
public sealed record DotaRecent(int Matches, int Wins, string Form, int Streak, double Kills, double Deaths, double Assists, int Gpm, int Xpm,
    int SoloGames, int SoloWins, int PartyGames, int PartyWins, int RankedGames, int RankedWins);

/// <param name="Key">"1"–"5" for positions (STRATZ), or "safe", "mid", "off", "jungle", "roam" for lanes (OpenDota).</param>
public sealed record DotaRole(string Key, int Games, int Wins);

/// <param name="AccountId">Dota's 32-bit account id.</param>
/// <param name="Games">Games on the same team.</param>
public sealed record DotaPeer(long AccountId, int Games, int Wins);

public sealed record DotaRankPoint(DateOnly Day, int RankTier);

public sealed record Cs2Map(string Name, int Matches, double? WinRate);

/// <param name="Level">FACEIT skill level, 1–10.</param>
public sealed record Cs2Profile(string FaceitId, string Nickname, int? Level, int? Elo, int Matches, double? WinRate, double? KillDeath,
    double? Headshots, IReadOnlyList<Cs2Map> Maps);

/// <summary>
/// A fetched card for one person and game. <see cref="Payload"/> is the profile as JSON when there is one;
/// <see cref="Reason"/> says why the last lookup didn't give a card (also written to the logs).
/// </summary>
public sealed record CachedStats(ulong UserId, StatsGame Game, StatsStatus Status, string? Payload, DateTimeOffset FetchedAt, string? Reason = null);

/// <param name="Reason">Why there's no profile: the service's answer, a missing setting, or what the player can fix.</param>
public sealed record StatsFetch<T>(StatsStatus Status, T? Profile, string? Reason = null) where T : class;

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

    Task<bool> GetDigestChannelAsync(ulong guildId, Guid groupId);
    Task SetDigestChannelAsync(ulong guildId, Guid groupId, bool post);
    /// <summary>Claims a group's digest for the week starting <paramref name="weekStart"/>; false when already claimed.</summary>
    Task<bool> MarkDigestAsync(ulong guildId, Guid groupId, DateOnly weekStart);
}

/// <summary>Posts a group's match recap in its notification channel. Best effort.</summary>
public interface IRecapPublisher
{
    Task PublishAsync(ulong guildId, string groupName, GroupRecap recap, CancellationToken cancellationToken);

    /// <summary>Posts last week's summary (<paramref name="weekStart"/> is that week's Monday) in the group's channel.</summary>
    Task PublishDigestAsync(ulong guildId, Guid groupId, string groupName, DateOnly weekStart,
        IReadOnlyList<HappyMix.Application.Stats.GroupDigest> digests, CancellationToken cancellationToken);
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
