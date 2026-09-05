using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Domain.CounterStrike;
using Microsoft.Extensions.Options;

namespace HappyMix.Infrastructure.PandaScore;

internal sealed class PandaScoreClient : IPandaScoreClient
{
    private readonly HttpClient _httpClient;
    private readonly PandaScoreOptions _options;
    private readonly TimeZoneInfo _timeZone;

    public PandaScoreClient(HttpClient httpClient, IOptions<PandaScoreOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId);
    }

    public async Task<CounterStrikeMatchDay> GetMatchesForDayAsync(
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(referenceTime, _timeZone).DateTime);
        DateTimeOffset start = GetUtcBoundary(date);
        DateTimeOffset end = GetUtcBoundary(date.AddDays(1));

        Task<IReadOnlyCollection<PandaScoreMatch>> upcoming = GetMatchesAsync(
            "upcoming", start, end, cancellationToken);
        Task<IReadOnlyCollection<PandaScoreMatch>> running = GetMatchesAsync(
            "running", start, end, cancellationToken);
        Task<IReadOnlyCollection<PandaScoreMatch>> past = GetMatchesAsync(
            "past", start, end, cancellationToken);

        await Task.WhenAll(upcoming, running, past);

        var matches = upcoming.Result
            .Concat(running.Result)
            .Concat(past.Result)
            .GroupBy(match => match.Id)
            .Select(group => group.Last())
            .Where(match => match.Tournament?.Tier is { } tier &&
                _options.IncludedTournamentTiers.Contains(tier, StringComparer.OrdinalIgnoreCase))
            .Select(match => match.ToDomain())
            .OrderBy(match => match.BeginAt)
            .ToArray();

        return new CounterStrikeMatchDay(date, matches);
    }

    private async Task<IReadOnlyCollection<PandaScoreMatch>> GetMatchesAsync(
        string status,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        string range = string.Create(
            CultureInfo.InvariantCulture,
            $"{start:O},{end:O}");
        string path = string.Create(
            CultureInfo.InvariantCulture,
            $"csgo/matches/{status}?filter[videogame_title]=cs-2&range[begin_at]={Uri.EscapeDataString(range)}&sort=begin_at&per_page=100");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PandaScoreMatch[]>(cancellationToken)
            ?? [];
    }

    private DateTimeOffset GetUtcBoundary(DateOnly date)
    {
        DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _timeZone));
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException(
                "A PandaScore API token is required. Set the PandaScore__Token environment variable.");
        }
    }
}

internal sealed record PandaScoreMatch(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("begin_at")] DateTimeOffset? BeginAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("league")] PandaScoreNamedResource? League,
    [property: JsonPropertyName("tournament")] PandaScoreTournament? Tournament,
    [property: JsonPropertyName("opponents")] IReadOnlyCollection<PandaScoreOpponentSlot>? Opponents,
    [property: JsonPropertyName("results")] IReadOnlyCollection<PandaScoreResult>? Results,
    [property: JsonPropertyName("games")] IReadOnlyCollection<PandaScoreGame>? Games,
    [property: JsonPropertyName("winner_id")] long? WinnerId)
{
    public CounterStrikeMatch ToDomain() => new(
        Id,
        Name,
        BeginAt,
        Status,
        League?.Name ?? "Unknown league",
        Tournament?.Name ?? "Unknown tournament",
        Opponents?.Select(slot => new CounterStrikeOpponent(
            slot.Opponent.Id,
            slot.Opponent.Name)).ToArray() ?? [],
        Results?.Select(result => new CounterStrikeTeamResult(
            result.TeamId,
            result.Score)).ToArray() ?? [],
        Games?.Select(game => new CounterStrikeGame(
            game.Id,
            game.Position,
            game.Status,
            game.Complete,
            game.Winner?.Id)).ToArray() ?? [],
        WinnerId,
        Tournament?.Id);
}

internal sealed record PandaScoreTournament(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tier")] string? Tier);

internal sealed record PandaScoreNamedResource(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record PandaScoreOpponentSlot(
    [property: JsonPropertyName("opponent")] PandaScoreNamedResource Opponent);

internal sealed record PandaScoreResult(
    [property: JsonPropertyName("team_id")] long TeamId,
    [property: JsonPropertyName("score")] int Score);

internal sealed record PandaScoreGame(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("winner")] PandaScoreGameWinner? Winner);

internal sealed record PandaScoreGameWinner(
    [property: JsonPropertyName("id")] long? Id);
