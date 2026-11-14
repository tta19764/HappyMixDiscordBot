using HappyMix.Domain.Esports;

namespace HappyMix.Application.Abstractions.Esports;

/// <summary>A team as the esports data provider names it.</summary>
public sealed record EsportsTeam(long Id, string Name, string? Acronym, string? Location, string? ImageUrl);

/// <summary>Looks teams up by name or by ID so people can follow teams without knowing provider IDs.</summary>
public interface IEsportsTeamDirectory
{
    /// <summary>Teams of one game whose name contains <paramref name="query"/>, best matches first.</summary>
    Task<IReadOnlyList<EsportsTeam>> SearchAsync(Esport game, string query, CancellationToken cancellationToken);

    /// <summary>The known teams among <paramref name="ids"/>; unknown IDs are left out.</summary>
    Task<IReadOnlyList<EsportsTeam>> GetAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
}
