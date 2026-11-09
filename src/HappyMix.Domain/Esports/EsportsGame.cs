namespace HappyMix.Domain.Esports;

/// <summary>A CS2 map or Dota 2 game within a series.</summary>
/// <param name="Id">Provider game ID scoped to its discipline.</param>
/// <param name="Position">One-based position within the series.</param>
/// <param name="Status">Provider lifecycle status.</param>
/// <param name="Complete">Whether a final game result is available.</param>
/// <param name="WinnerId">Winning team, if known.</param>
public sealed record EsportsGame(
    long Id,
    int Position,
    string Status,
    bool Complete,
    long? WinnerId);
