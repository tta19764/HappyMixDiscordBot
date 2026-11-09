using HappyMix.Domain.Esports;

namespace HappyMix.Application.Abstractions.Esports;

/// <summary>Supplies followed main-team matches across supported disciplines.</summary>
public interface IPandaScoreClient
{
    /// <summary>Loads a combined local-date schedule, including running series crossing midnight.</summary>
    /// <param name="referenceTime">Instant used to select the local schedule date.</param>
    /// <param name="cancellationToken">Cancels provider requests.</param>
    /// <returns>The selected date and followed series.</returns>
    Task<EsportsMatchDay> GetMatchesForDayAsync(
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken);
}
