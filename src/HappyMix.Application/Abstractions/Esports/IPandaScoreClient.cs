using HappyMix.Domain.CounterStrike;

namespace HappyMix.Application.Abstractions.Esports;

public interface IPandaScoreClient
{
    Task<CounterStrikeMatchDay> GetMatchesForDayAsync(
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken);
}
