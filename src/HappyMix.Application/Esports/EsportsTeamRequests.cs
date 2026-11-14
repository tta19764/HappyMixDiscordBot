using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;
using HappyMix.Domain.Esports;

namespace HappyMix.Application.Esports;

/// <summary>Searches one game's teams by name, for the dashboard's "add a team" box.</summary>
public sealed record SearchEsportsTeamsQuery(Esport Game, string Text) : IQuery<IReadOnlyList<EsportsTeam>>;

/// <summary>Names for followed team IDs.</summary>
public sealed record GetEsportsTeamsQuery(IReadOnlyCollection<long> Ids) : IQuery<IReadOnlyList<EsportsTeam>>;

/// <summary>
/// Turns one typed entry into a team: a number or "Name #123" is taken as the ID, a name must match exactly one
/// team's name or acronym. When it doesn't, <see cref="TeamResolution.Team"/> is null and
/// <see cref="TeamResolution.Candidates"/> lists the closest teams (empty when nothing matched).
/// </summary>
public sealed record ResolveEsportsTeamQuery(Esport Game, string Text) : IQuery<TeamResolution>;

public sealed record TeamResolution(EsportsTeam? Team, IReadOnlyList<EsportsTeam> Candidates);

internal sealed class EsportsTeamHandlers(IEsportsTeamDirectory directory) :
    IQueryHandler<SearchEsportsTeamsQuery, IReadOnlyList<EsportsTeam>>,
    IQueryHandler<GetEsportsTeamsQuery, IReadOnlyList<EsportsTeam>>,
    IQueryHandler<ResolveEsportsTeamQuery, TeamResolution>
{
    internal const int MinimumSearchLength = 2;

    public async Task<Result<IReadOnlyList<EsportsTeam>>> Handle(SearchEsportsTeamsQuery query, CancellationToken cancellationToken)
    {
        string text = query.Text.Trim();
        if (text.Length < MinimumSearchLength || text.Length > 60)
            return Result.Success<IReadOnlyList<EsportsTeam>>([]);
        try
        {
            return Result.Success(await directory.SearchAsync(query.Game, text, cancellationToken));
        }
        catch (Exception exception) when (Unavailable(exception, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<EsportsTeam>>(UnavailableError);
        }
    }

    // Names are a nicety: when the provider is down, callers fall back to showing the IDs.
    public async Task<Result<IReadOnlyList<EsportsTeam>>> Handle(GetEsportsTeamsQuery query, CancellationToken cancellationToken)
    {
        if (query.Ids.Count == 0)
            return Result.Success<IReadOnlyList<EsportsTeam>>([]);
        try
        {
            return Result.Success(await directory.GetAsync(query.Ids.Distinct().ToArray(), cancellationToken));
        }
        catch (Exception exception) when (Unavailable(exception, cancellationToken))
        {
            return Result.Success<IReadOnlyList<EsportsTeam>>([]);
        }
    }

    public async Task<Result<TeamResolution>> Handle(ResolveEsportsTeamQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return Result.Success(await ResolveAsync(query, cancellationToken));
        }
        catch (Exception exception) when (Unavailable(exception, cancellationToken))
        {
            return Result.Failure<TeamResolution>(UnavailableError);
        }
    }

    private static readonly ErrorDescriptor UnavailableError = new("Esports.Unavailable",
        "Team search isn't available right now. Add the team's number like \"Team name #123\", or try again later.");

    private static bool Unavailable(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is HttpRequestException or InvalidOperationException or TaskCanceledException;

    private async Task<TeamResolution> ResolveAsync(ResolveEsportsTeamQuery query, CancellationToken cancellationToken)
    {
        string text = query.Text.Trim();
        // An ID is trusted as typed, so a provider outage never blocks saving teams that are already known.
        if (TeamText.TryGetId(text, out long id))
            return new(new EsportsTeam(id, TeamText.NameOf(text) ?? $"#{id}", null, null, null), []);

        var found = text.Length < MinimumSearchLength ? [] : await directory.SearchAsync(query.Game, text, cancellationToken);
        var exact = found.Where(team => string.Equals(team.Name, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(team.Acronym, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        var candidates = exact.Length > 0 ? exact : found.Count == 1 ? found.ToArray() : [];
        if (candidates.Length == 1)
            return new(candidates[0], []);
        return new(null, (candidates.Length > 1 ? candidates : found).Take(4).ToArray());
    }
}

/// <summary>The text form of a followed team: "Team Vitality #3455". The number after # is what counts.</summary>
public static class TeamText
{
    public static string Format(EsportsTeam team) => $"{team.Name} #{team.Id}";

    public static string Describe(EsportsTeam team) =>
        team.Location is { Length: > 0 } location ? $"{team.Name} #{team.Id} ({location})" : $"{team.Name} #{team.Id}";

    public static bool TryGetId(string text, out long id)
    {
        int hash = text.LastIndexOf('#');
        string number = hash >= 0 ? text[(hash + 1)..].Trim() : text.Trim();
        return long.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;
    }

    public static string? NameOf(string text)
    {
        int hash = text.LastIndexOf('#');
        return hash > 0 && text[..hash].Trim() is { Length: > 0 } name ? name : null;
    }
}
