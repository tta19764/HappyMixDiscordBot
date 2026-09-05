namespace HappyMix.Domain.CounterStrike;

public sealed record CounterStrikeMatchDay(
    DateOnly Date,
    IReadOnlyCollection<CounterStrikeMatch> Matches);

public sealed record CounterStrikeMatch(
    long Id,
    string Name,
    DateTimeOffset? BeginAt,
    string Status,
    string LeagueName,
    string TournamentName,
    IReadOnlyCollection<CounterStrikeOpponent> Opponents,
    IReadOnlyCollection<CounterStrikeTeamResult> Results,
    IReadOnlyCollection<CounterStrikeGame> Games,
    long? WinnerId,
    long? TournamentId = null)
{
    public bool IsRunning => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);

    public bool IsFinished => string.Equals(Status, "finished", StringComparison.OrdinalIgnoreCase);
}
public sealed record CounterStrikeOpponent(long Id, string Name);

public sealed record CounterStrikeTeamResult(long TeamId, int Score);
