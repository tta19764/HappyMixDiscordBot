namespace HappyMix.Domain.Esports;

/// <summary>A schedule combining followed disciplines for one local date.</summary>
/// <param name="Date">Date in the configured timezone.</param>
/// <param name="Matches">Matches identified by discipline and provider ID.</param>
public sealed record EsportsMatchDay(
    DateOnly Date,
    IReadOnlyCollection<EsportsMatch> Matches);

/// <summary>A provider-neutral series and its individual map/game results.</summary>
/// <param name="Id">Provider series ID scoped to its discipline.</param>
/// <param name="Name">Fallback matchup name.</param>
/// <param name="BeginAt">Scheduled start, if known.</param>
/// <param name="Status">Provider lifecycle status.</param>
/// <param name="LeagueName">League label.</param>
/// <param name="TournamentName">Tournament label.</param>
/// <param name="Opponents">Known participating teams.</param>
/// <param name="Results">Series scores by team.</param>
/// <param name="Games">Maps in CS2 or games in Dota 2.</param>
/// <param name="WinnerId">Winning team, if known.</param>
/// <param name="TournamentId">Provider tournament ID, if known.</param>
/// <param name="Game">Discipline, which namespaces provider identifiers.</param>
public sealed record EsportsMatch(
    long Id,
    string Name,
    DateTimeOffset? BeginAt,
    string Status,
    string LeagueName,
    string TournamentName,
    IReadOnlyCollection<EsportsOpponent> Opponents,
    IReadOnlyCollection<EsportsTeamResult> Results,
    IReadOnlyCollection<EsportsGame> Games,
    long? WinnerId,
    long? TournamentId = null,
    Esport Game = Esport.CounterStrike2)
{
    /// <summary>Whether this series is in progress.</summary>
    public bool IsRunning => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this series has finished.</summary>
    public bool IsFinished => string.Equals(Status, "finished", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this series has a confirmed upcoming lifecycle status.</summary>
    public bool IsScheduled => string.Equals(Status, "not_started", StringComparison.OrdinalIgnoreCase);
}
/// <summary>A team identified by the provider.</summary>
/// <param name="Id">Provider team ID.</param>
/// <param name="Name">Display name, never used for subscription matching.</param>
public sealed record EsportsOpponent(long Id, string Name);

/// <summary>A team's series score.</summary>
/// <param name="TeamId">Provider team ID.</param>
/// <param name="Score">Maps/games won.</param>
public sealed record EsportsTeamResult(long TeamId, int Score);
