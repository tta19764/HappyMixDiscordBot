using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Domain.Esports;
using Microsoft.Extensions.Options;

namespace HappyMix.Infrastructure.PandaScore;

/// <summary>Loads both disciplines and follows exact game-specific main-team IDs.</summary>
internal sealed class PandaScoreClient : IPandaScoreClient, IGuildMatchFeed
{
    private const int PageSize = 100;
    private const int MaxPages = 100;
    private readonly HttpClient _httpClient;
    private readonly PandaScoreOptions _options;
    private readonly TimeZoneInfo _timeZone;

    /// <summary>Creates a client using the configured timezone and roster allowlists.</summary>
    /// <param name="httpClient">HTTP transport with the provider base address.</param>
    /// <param name="options">Provider token, local timezone and exact team allowlists.</param>
    public PandaScoreClient(HttpClient httpClient, IOptions<PandaScoreOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId);
    }

    /// <inheritdoc />
    public Task<EsportsMatchDay> GetMatchesForDayAsync(
        DateTimeOffset referenceTime, CancellationToken cancellationToken) =>
        GetMatchesAsync(referenceTime, _timeZone, GuildEsportsPreferences.Default, false, cancellationToken);

    public Task<EsportsMatchDay> GetMatchesAsync(DateTimeOffset now, string timeZoneId,
        GuildEsportsPreferences preferences, CancellationToken cancellationToken) =>
        GetMatchesAsync(now, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), preferences, true, cancellationToken);

    private async Task<EsportsMatchDay> GetMatchesAsync(DateTimeOffset referenceTime, TimeZoneInfo timeZone,
        GuildEsportsPreferences preferences, bool includeReminderLookahead, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException(
                "A PandaScore API token is required. Set PandaScore__Token.");
        }

        DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(referenceTime, timeZone).DateTime);
        DateTimeOffset start = GetUtcBoundary(date, timeZone);
        DateTimeOffset end = GetUtcBoundary(date.AddDays(1), timeZone);
        var reminderEnd = referenceTime.AddMinutes(preferences.ReminderMinutes).AddTicks(1);
        var upcomingEnd = includeReminderLookahead && preferences.Notifications.HasFlag(EsportsNotifications.Reminders)
            && reminderEnd > end ? reminderEnd : end;
        var disciplines = new[]
        {
            (Game: Esport.CounterStrike2, Path: "csgo", Teams: preferences.CounterStrikeTeamIds ?? _options.CounterStrikeTeamIds ??
                GetAliasIds(preferences.CounterStrikeTeamAliases ?? _options.CounterStrikeTeamAliases,
                    [3216, 136955, 131441, 129381, 136497, 3210])),
            (Game: Esport.Dota2, Path: "dota2", Teams: preferences.Dota2TeamIds ?? _options.Dota2TeamIds ??
                GetAliasIds(preferences.Dota2TeamAliases ?? _options.Dota2TeamAliases, [1699, 132557]))
        };
        var matches = new List<EsportsMatch>();
        // Keep requests sequential to avoid bursts against the provider's minute limit.
        foreach (var discipline in disciplines)
        {
            if (discipline.Teams.Length == 0)
            {
                continue;
            }

            var teamIds = discipline.Teams.ToHashSet();
            var snapshots = new List<PandaScoreMatch>();
            foreach (string status in new[] { "upcoming", "running", "past" })
            {
                snapshots.AddRange(await GetMatchesAsync(
                    discipline.Path, status, start, status == "upcoming" ? upcomingEnd : end, cancellationToken));
            }

            matches.AddRange(snapshots.GroupBy(match => match.Id)
                .Select(group => group.Last())
                .Where(match => match.Opponents?.Any(slot =>
                    slot.Opponent is { } opponent && teamIds.Contains(opponent.Id)) == true)
                .Where(match => _options.IncludedTournamentTiers.Length == 0 ||
                    match.Tournament?.Tier is { } tier &&
                    _options.IncludedTournamentTiers.Contains(tier, StringComparer.OrdinalIgnoreCase))
                .Select(match => match.ToDomain(discipline.Game)));
        }

        return new EsportsMatchDay(date, matches.OrderBy(match => match.BeginAt)
            .ThenBy(match => match.Game).ThenBy(match => match.Id).ToArray());
    }

    /// <summary>
    /// Paginates upcoming starts and completed series by local date. Running series
    /// have no date restriction so matches that cross midnight are not lost.
    /// </summary>
    private async Task<IReadOnlyCollection<PandaScoreMatch>> GetMatchesAsync(
        string game, string status, DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        string titleFilter = game == "csgo" ? "&filter[videogame_title]=cs-2" : string.Empty;
        string dateFilter = status == "running" ? string.Empty :
            string.Create(CultureInfo.InvariantCulture,
                $"&range[{(status == "past" ? "end_at" : "begin_at")}]={Uri.EscapeDataString($"{start:O},{end:O}")}");
        var matches = new List<PandaScoreMatch>();
        for (int page = 1; page <= MaxPages; page++)
        {
            string path = string.Create(CultureInfo.InvariantCulture,
                $"{game}/matches/{status}?sort=id&per_page={PageSize}&page={page}{titleFilter}{dateFilter}");
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var batch = await response.Content.ReadFromJsonAsync<PandaScoreMatch[]>(cancellationToken)
                ?? throw new InvalidOperationException("PandaScore returned null instead of a match list.");
            // The API range can include its upper boundary; enforce half-open local days.
            matches.AddRange(batch.Where(match => status == "running" ||
                (status == "past" ? match.EndAt : match.BeginAt) is { } time &&
                time >= start && time < end));
            if (batch.Length < PageSize)
            {
                return matches;
            }
        }

        throw new InvalidOperationException("PandaScore pagination exceeded its safety limit; refusing a truncated schedule.");
    }

    private static long[] GetAliasIds(Dictionary<string, long> aliases, long[] fallback) =>
        aliases.Count > 0 ? aliases.Values.ToArray() : fallback;

    private static DateTimeOffset GetUtcBoundary(DateOnly date, TimeZoneInfo timeZone)
    {
        DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        if (timeZone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, timeZone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }
}

internal sealed record PandaScoreMatch(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("begin_at")] DateTimeOffset? BeginAt,
    [property: JsonPropertyName("end_at")] DateTimeOffset? EndAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("league")] PandaScoreNamedResource? League,
    [property: JsonPropertyName("tournament")] PandaScoreTournament? Tournament,
    [property: JsonPropertyName("opponents")] IReadOnlyCollection<PandaScoreOpponentSlot>? Opponents,
    [property: JsonPropertyName("results")] IReadOnlyCollection<PandaScoreResult>? Results,
    [property: JsonPropertyName("games")] IReadOnlyCollection<PandaScoreGame>? Games,
    [property: JsonPropertyName("winner_id")] long? WinnerId,
    [property: JsonPropertyName("streams_list")] IReadOnlyList<PandaScoreStream>? Streams)
{
    public EsportsMatch ToDomain(Esport game) => new(
        Id, Name, BeginAt, Status,
        League?.Name ?? "Unknown league", Tournament?.Name ?? "Unknown tournament",
        Opponents?.Where(slot => slot.Opponent is not null)
            .Select(slot => new EsportsOpponent(slot.Opponent!.Id, slot.Opponent.Name)).ToArray() ?? [],
        Results?.Select(result => new EsportsTeamResult(result.TeamId, result.Score)).ToArray() ?? [],
        Games?.Select(item => new EsportsGame(
            item.Id, item.Position, item.Status, item.Complete, item.Winner?.Id)).ToArray() ?? [],
        WinnerId, Tournament?.Id, game, Streams?.Select(stream => stream.RawUrl).OfType<string>().ToArray());
}

internal sealed record PandaScoreStream([property: JsonPropertyName("raw_url")] string? RawUrl);

internal sealed record PandaScoreTournament(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tier")] string? Tier);

internal sealed record PandaScoreNamedResource(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record PandaScoreOpponentSlot(
    [property: JsonPropertyName("opponent")] PandaScoreNamedResource? Opponent);

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
