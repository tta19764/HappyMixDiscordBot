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

/// <summary>Stats sharing choices and the card cache.</summary>
public interface IGameStatsStore
{
    Task<IReadOnlySet<ulong>> ListSharingAsync(IReadOnlyCollection<ulong> userIds);
    Task<IReadOnlyList<ulong>> ListAllSharingAsync();
    Task SetSharingAsync(ulong userId, bool share, DateTimeOffset at);
    Task<IReadOnlyList<CachedStats>> ListCachedAsync(IReadOnlyCollection<ulong> userIds);
    Task SaveCachedAsync(CachedStats stats);
    Task RemoveCachedAsync(ulong userId);
}

/// <summary>Public stats services: OpenDota for Dota 2 and FACEIT for CS2.</summary>
public interface IGameStatsDirectory
{
    /// <summary>Whether CS2 cards can be fetched (a FACEIT key is configured).</summary>
    bool Cs2Enabled { get; }
    Task<StatsFetch<DotaProfile>> GetDotaAsync(string steamId64, CancellationToken cancellationToken);
    Task<StatsFetch<Cs2Profile>> GetCs2Async(string steamId64, CancellationToken cancellationToken);
}
