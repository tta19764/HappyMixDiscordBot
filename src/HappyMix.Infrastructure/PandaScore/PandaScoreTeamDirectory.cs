using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Domain.Esports;
using Microsoft.Extensions.Options;

namespace HappyMix.Infrastructure.PandaScore;

/// <summary>
/// PandaScore team lookups for the dashboard. Results are cached in memory so typing in the search box and
/// reopening settings stay well inside the provider's request limit.
/// </summary>
internal sealed class PandaScoreTeamDirectory(IHttpClientFactory httpClientFactory, IOptions<PandaScoreOptions> options, TimeProvider time)
    : IEsportsTeamDirectory
{
    internal const string HttpClientName = "PandaScore.Teams";
    private static readonly TimeSpan SearchLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TeamLifetime = TimeSpan.FromHours(12);
    private const int SearchSize = 20;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, EsportsTeam[] Teams)> _searches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, (DateTimeOffset Expires, EsportsTeam? Team)> _teams = new();

    public async Task<IReadOnlyList<EsportsTeam>> SearchAsync(Esport game, string query, CancellationToken cancellationToken)
    {
        string text = query.Trim();
        string key = $"{game}:{text}";
        var now = time.GetUtcNow();
        if (_searches.TryGetValue(key, out var cached) && cached.Expires > now)
            return cached.Teams;

        string path = string.Create(CultureInfo.InvariantCulture,
            $"{(game == Esport.Dota2 ? "dota2" : "csgo")}/teams?search[name]={Uri.EscapeDataString(text)}&per_page={SearchSize}");
        var teams = (await GetTeamsAsync(path, cancellationToken))
            .OrderBy(team => Rank(team, text)).ThenBy(team => team.Name.Length).ThenBy(team => team.Id).ToArray();
        foreach (var team in teams)
            _teams[team.Id] = (now + TeamLifetime, team);
        if (_searches.Count > 500)
            _searches.Clear();
        _searches[key] = (now + SearchLifetime, teams);
        return teams;
    }

    public async Task<IReadOnlyList<EsportsTeam>> GetAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var missing = ids.Where(id => !_teams.TryGetValue(id, out var cached) || cached.Expires <= now).Distinct().ToArray();
        foreach (var chunk in missing.Chunk(50))
        {
            string path = string.Create(CultureInfo.InvariantCulture,
                $"teams?filter[id]={string.Join(',', chunk)}&per_page={chunk.Length}");
            var found = (await GetTeamsAsync(path, cancellationToken)).ToDictionary(team => team.Id);
            foreach (long id in chunk)
                _teams[id] = (now + TeamLifetime, found.GetValueOrDefault(id));
        }
        return ids.Select(id => _teams.TryGetValue(id, out var cached) ? cached.Team : null).OfType<EsportsTeam>().ToArray();
    }

    // Exact name or acronym first, then names starting with the text, then everything else.
    private static int Rank(EsportsTeam team, string text) =>
        string.Equals(team.Name, text, StringComparison.OrdinalIgnoreCase) || string.Equals(team.Acronym, text, StringComparison.OrdinalIgnoreCase) ? 0
        : team.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private async Task<EsportsTeam[]> GetTeamsAsync(string path, CancellationToken cancellationToken)
    {
        string? token = options.Value.Token;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("A PandaScore API token is required. Set PandaScore__Token.");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var teams = await response.Content.ReadFromJsonAsync<PandaScoreTeam[]>(cancellationToken) ?? [];
        return teams.Where(team => team.Id > 0 && !string.IsNullOrWhiteSpace(team.Name))
            .Select(team => new EsportsTeam(team.Id, team.Name.Trim(), Blank(team.Acronym), Blank(team.Location), Blank(team.ImageUrl)))
            .ToArray();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PandaScoreTeam(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("acronym")] string? Acronym,
        [property: JsonPropertyName("location")] string? Location,
        [property: JsonPropertyName("image_url")] string? ImageUrl);
}
