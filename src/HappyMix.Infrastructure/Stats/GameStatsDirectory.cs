using System.Globalization;
using System.Net;
using System.Text.Json;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Stats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HappyMix.Infrastructure.Stats;

/// <summary>
/// Public player stats. Dota 2 comes from OpenDota (no key needed; <c>OpenDota:ApiKey</c> raises its limits), CS2 from
/// FACEIT's Data API (needs <c>Faceit:ApiKey</c>). Every lookup is best effort: errors come back as
/// <see cref="StatsStatus.Error"/> and are logged, never thrown.
/// </summary>
internal sealed partial class GameStatsDirectory(IHttpClientFactory clients, IConfiguration configuration, ILogger<GameStatsDirectory> logger)
    : IGameStatsDirectory, IDisposable
{
    public const string OpenDotaClient = "OpenDota";
    public const string FaceitClient = "Faceit";
    private const string OpenDota = "https://api.opendota.com/api/";
    private const string Faceit = "https://open.faceit.com/data/v4/";
    private const string SteamCdn = "https://cdn.cloudflare.steamstatic.com";
    private static readonly TimeSpan HeroesFor = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim heroesLock = new(1);
    private IReadOnlyDictionary<int, (string Name, string? Image)>? heroes;
    private DateTimeOffset heroesAt;

    private string? FaceitKey => configuration["Faceit:ApiKey"] is { Length: > 0 } key ? key : null;
    private string? OpenDotaKey => configuration["OpenDota:ApiKey"] is { Length: > 0 } key ? key : null;

    public bool Cs2Enabled => FaceitKey is not null;

    public void Dispose() => heroesLock.Dispose();

    public async Task<StatsFetch<DotaProfile>> GetDotaAsync(string steamId64, CancellationToken cancellationToken)
    {
        if (GameStats.DotaAccountId(steamId64) is not { } accountId)
            return new(StatsStatus.NoAccount, null);
        try
        {
            using var client = clients.CreateClient(OpenDotaClient);
            string id = accountId.ToString(CultureInfo.InvariantCulture);
            using var player = await GetJsonAsync(client, OpenDotaUrl($"players/{id}"), cancellationToken);
            if (player is null)
                return new(StatsStatus.Error, null);
            using var record = await GetJsonAsync(client, OpenDotaUrl($"players/{id}/wl", "date=30"), cancellationToken);
            using var played = await GetJsonAsync(client, OpenDotaUrl($"players/{id}/heroes", "date=90"), cancellationToken);
            if (record is null || played is null)
                return new(StatsStatus.Error, null);
            var profile = ParseDota(accountId, player.RootElement, record.RootElement, played.RootElement, await HeroesAsync(client, cancellationToken));
            return profile is null ? new(StatsStatus.NoPublicData, null) : new(StatsStatus.Ok, profile);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogLookupFailed(logger, "OpenDota", error);
            return new(StatsStatus.Error, null);
        }
    }

    public async Task<StatsFetch<Cs2Profile>> GetCs2Async(string steamId64, CancellationToken cancellationToken)
    {
        if (FaceitKey is not { } key)
            return new(StatsStatus.NoAccount, null);
        try
        {
            using var client = clients.CreateClient(FaceitClient);
            using var lookup = new HttpRequestMessage(HttpMethod.Get, $"{Faceit}players?game=cs2&game_player_id={Uri.EscapeDataString(steamId64)}");
            lookup.Headers.Authorization = new("Bearer", key);
            using var found = await client.SendAsync(lookup, cancellationToken);
            if (found.StatusCode == HttpStatusCode.NotFound)
                return new(StatsStatus.NoAccount, null);
            if (!found.IsSuccessStatusCode)
                return new(StatsStatus.Error, null);
            using var player = JsonDocument.Parse(await found.Content.ReadAsStringAsync(cancellationToken));
            if (Text(player.RootElement, "player_id") is not { } faceitId)
                return new(StatsStatus.NoAccount, null);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Faceit}players/{Uri.EscapeDataString(faceitId)}/stats/cs2");
            request.Headers.Authorization = new("Bearer", key);
            using var answer = await client.SendAsync(request, cancellationToken);
            // A FACEIT account that never played CS2 has no stats; its card still shows level and links.
            using var stats = answer.IsSuccessStatusCode ? JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken)) : null;
            return new(StatsStatus.Ok, ParseCs2(player.RootElement, stats?.RootElement));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogLookupFailed(logger, "FACEIT", error);
            return new(StatsStatus.Error, null);
        }
    }

    /// <summary>
    /// A Dota card from OpenDota's player, win/loss and heroes answers. Null when the account has no public data:
    /// OpenDota then knows no rank and no matches.
    /// </summary>
    internal static DotaProfile? ParseDota(long accountId, JsonElement player, JsonElement record, JsonElement played,
        IReadOnlyDictionary<int, (string Name, string? Image)> heroes)
    {
        int? rank = Int(player, "rank_tier");
        int wins = Int(record, "win") ?? 0, losses = Int(record, "lose") ?? 0;
        var top = played.ValueKind != JsonValueKind.Array ? [] : played.EnumerateArray()
            .Select(hero => (Id: Int(hero, "hero_id") ?? 0, Games: Int(hero, "games") ?? 0, Wins: Int(hero, "win") ?? 0))
            .Where(hero => hero.Id > 0 && hero.Games > 0)
            .OrderByDescending(hero => hero.Games).ThenByDescending(hero => hero.Wins).Take(3)
            .Select(hero => heroes.TryGetValue(hero.Id, out var known)
                ? new DotaHero(hero.Id, known.Name, known.Image, hero.Games, hero.Wins)
                : new DotaHero(hero.Id, "#" + hero.Id.ToString(CultureInfo.InvariantCulture), null, hero.Games, hero.Wins))
            .ToArray();
        if (rank is null && wins + losses == 0 && top.Length == 0)
            return null;
        string? persona = player.TryGetProperty("profile", out var profile) ? Text(profile, "personaname") : null;
        return new DotaProfile(accountId, persona, rank, Int(player, "leaderboard_rank"), wins, losses, top);
    }

    /// <summary>OpenDota's hero list: names and pictures on Steam's CDN.</summary>
    internal static IReadOnlyDictionary<int, (string Name, string? Image)> ParseHeroes(JsonElement constants)
    {
        var result = new Dictionary<int, (string Name, string? Image)>();
        if (constants.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var hero in constants.EnumerateObject())
            if (Int(hero.Value, "id") is { } id && Text(hero.Value, "localized_name") is { } name)
                result[id] = (name, Text(hero.Value, "img") is { Length: > 0 } image && image.StartsWith('/') ? SteamCdn + image.TrimEnd('?') : null);
        return result;
    }

    /// <summary>A CS2 card from FACEIT's player lookup and (when it exists) the player's CS2 stats.</summary>
    internal static Cs2Profile ParseCs2(JsonElement player, JsonElement? stats)
    {
        JsonElement cs2 = default;
        bool hasGame = player.TryGetProperty("games", out var games) && games.TryGetProperty("cs2", out cs2);
        var lifetime = stats is { } root && root.TryGetProperty("lifetime", out var life) ? life : default;
        var maps = stats is { } all && all.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array
            ? segments.EnumerateArray()
                .Where(segment => Text(segment, "type") == "Map" && Text(segment, "label") is { Length: > 0 })
                .Select(segment => segment.TryGetProperty("stats", out var values)
                    ? new Cs2Map(Text(segment, "label")!, Number(values, "Matches") is { } matches ? (int)matches : 0, Number(values, "Win Rate %"))
                    : null)
                .OfType<Cs2Map>().Where(map => map.Matches > 0).OrderByDescending(map => map.Matches).Take(2).ToArray()
            : [];
        return new Cs2Profile(Text(player, "player_id") ?? "", Text(player, "nickname") ?? "",
            hasGame ? Int(cs2, "skill_level") : null, hasGame ? Int(cs2, "faceit_elo") : null,
            lifetime.ValueKind == JsonValueKind.Object && Number(lifetime, "Matches") is { } total ? (int)total : 0,
            lifetime.ValueKind == JsonValueKind.Object ? Number(lifetime, "Win Rate %") : null,
            lifetime.ValueKind == JsonValueKind.Object ? Number(lifetime, "Average K/D Ratio") : null,
            lifetime.ValueKind == JsonValueKind.Object ? Number(lifetime, "Average Headshots %") : null,
            maps);
    }

    private async Task<IReadOnlyDictionary<int, (string Name, string? Image)>> HeroesAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (heroes is { } known && DateTimeOffset.UtcNow - heroesAt < HeroesFor)
            return known;
        await heroesLock.WaitAsync(cancellationToken);
        try
        {
            if (heroes is { } again && DateTimeOffset.UtcNow - heroesAt < HeroesFor)
                return again;
            using var document = await GetJsonAsync(client, OpenDotaUrl("constants/heroes"), cancellationToken);
            if (document is null)
                return heroes ?? new Dictionary<int, (string Name, string? Image)>();
            heroes = ParseHeroes(document.RootElement);
            heroesAt = DateTimeOffset.UtcNow;
            return heroes;
        }
        finally
        {
            heroesLock.Release();
        }
    }

    private string OpenDotaUrl(string path, string? query = null)
    {
        var parts = new List<string>();
        if (query is not null) parts.Add(query);
        if (OpenDotaKey is { } key) parts.Add("api_key=" + Uri.EscapeDataString(key));
        return OpenDota + path + (parts.Count > 0 ? "?" + string.Join('&', parts) : "");
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var answer = await client.GetAsync(url, cancellationToken);
        return answer.IsSuccessStatusCode ? JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken)) : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>Numbers that may come as JSON numbers or as strings (FACEIT sends "52" and "1.12").</summary>
    private static double? Number(JsonElement element, string name) =>
        element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed
        : null;

    private static int? Int(JsonElement element, string name) => Number(element, name) is { } value ? (int)Math.Round(value) : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} stats lookup failed")]
    private static partial void LogLookupFailed(ILogger logger, string service, Exception error);
}
