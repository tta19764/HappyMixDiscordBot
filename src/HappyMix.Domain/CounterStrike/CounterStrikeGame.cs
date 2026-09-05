namespace HappyMix.Domain.CounterStrike;

public sealed record CounterStrikeGame(
    long Id,
    int Position,
    string Status,
    bool Complete,
    long? WinnerId);
