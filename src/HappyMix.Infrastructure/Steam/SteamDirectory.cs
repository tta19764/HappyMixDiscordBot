using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Players;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HappyMix.Infrastructure.Steam;

/// <summary>
/// Steam lookups. Custom profile names resolve through the Web API when <c>Steam:ApiKey</c> is set, otherwise through the
/// public profile XML. Library imports need the key and a profile whose Game details are public. Sign-in checks follow
/// Steam's OpenID 2.0 flow: the response is sent back to Steam, which answers <c>is_valid:true</c> only for its own.
/// </summary>
internal sealed partial class SteamDirectory(IHttpClientFactory clients, IConfiguration configuration, ILogger<SteamDirectory> logger) : ISteamDirectory
{
    public const string HttpClientName = "Steam";
    private const string OpenIdEndpoint = SteamIds.OpenIdEndpoint;
    private const string IdentityPrefix = "https://steamcommunity.com/openid/id/";
    private string? ApiKey => configuration["Steam:ApiKey"] is { Length: > 0 } key ? key : null;

    public bool CanImport => ApiKey is not null;

    public async Task<string?> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        var (steamId, vanity) = Parse(input);
        if (steamId is not null || vanity is null)
            return steamId;
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            if (ApiKey is { } key)
            {
                using var document = JsonDocument.Parse(await client.GetStringAsync(
                    $"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key={Uri.EscapeDataString(key)}&vanityurl={Uri.EscapeDataString(vanity)}",
                    cancellationToken));
                var response = document.RootElement.GetProperty("response");
                return response.TryGetProperty("steamid", out var id) && SteamIds.IsSteamId64(id.GetString()) ? id.GetString() : null;
            }
            string xml = await client.GetStringAsync($"https://steamcommunity.com/id/{Uri.EscapeDataString(vanity)}/?xml=1", cancellationToken);
            return SteamId64Tag().Match(xml) is { Success: true } match ? match.Groups[1].Value : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            LogLookupFailed(logger, "profile", error);
            return null;
        }
    }

    public async Task<SteamLibrary> GetOwnedGamesAsync(string steamId, CancellationToken cancellationToken)
    {
        if (ApiKey is not { } key)
            return new SteamLibrary(SteamLibraryStatus.NotConfigured, []);
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            using var answer = await client.GetAsync(
                $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(key)}&steamid={steamId}&include_appinfo=1&include_played_free_games=1&format=json",
                cancellationToken);
            if (answer.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new SteamLibrary(SteamLibraryStatus.Private, []);
            answer.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken));
            var library = ParseLibrary(document.RootElement);
            if (library.Status != SteamLibraryStatus.Ok)
                return library;
            // Steam leaves some played free games (Dota 2, for one) out of the owned list, but lists them as recently
            // played. Best effort: without this answer the owned list is still imported.
            try
            {
                using var recentAnswer = await client.GetAsync(
                    $"https://api.steampowered.com/IPlayerService/GetRecentlyPlayedGames/v1/?key={Uri.EscapeDataString(key)}&steamid={steamId}&format=json",
                    cancellationToken);
                if (recentAnswer.IsSuccessStatusCode)
                {
                    using var recent = JsonDocument.Parse(await recentAnswer.Content.ReadAsStringAsync(cancellationToken));
                    return WithRecent(library, ParseLibrary(recent.RootElement));
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
            {
                LogLookupFailed(logger, "recently played", error);
            }
            return library;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogLookupFailed(logger, "library", error);
            return new SteamLibrary(SteamLibraryStatus.Unavailable, []);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPlayingAsync(IReadOnlyCollection<string> steamIds, CancellationToken cancellationToken)
    {
        var playing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ApiKey is not { } key || steamIds.Count == 0)
            return playing;
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            foreach (var batch in steamIds.Where(SteamIds.IsSteamId64).Chunk(100)) // Steam takes up to 100 accounts per call
            {
                using var document = JsonDocument.Parse(await client.GetStringAsync(
                    $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={string.Join(',', batch)}",
                    cancellationToken));
                foreach (var (id, game) in ParsePlaying(document.RootElement))
                    playing[id] = game;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogLookupFailed(logger, "playing", error);
        }
        return playing;
    }

    /// <summary>Accounts in a game, from GetPlayerSummaries: <c>gameextrainfo</c> is the game's name (public profiles only).</summary>
    internal static IEnumerable<(string SteamId, string Game)> ParsePlaying(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var player in players.EnumerateArray())
            if (player.TryGetProperty("steamid", out var id) && player.TryGetProperty("gameextrainfo", out var game) && game.GetString() is { Length: > 0 } name
                && SteamIds.IsSteamId64(id.GetString()))
                yield return (id.GetString()!, name);
    }

    public async Task<string?> VerifySignInAsync(IReadOnlyDictionary<string, string> response, string returnTo, CancellationToken cancellationToken)
    {
        if (!response.TryGetValue("openid.mode", out string? mode) || mode != "id_res"
            || !response.TryGetValue("openid.return_to", out string? target) || !SameReturn(target, returnTo)
            || !response.TryGetValue("openid.op_endpoint", out string? endpoint) || endpoint != OpenIdEndpoint
            || !response.TryGetValue("openid.claimed_id", out string? claimed) || ClaimedId(claimed) is not { } steamId)
            return null;
        try
        {
            var check = response.Where(pair => pair.Key.StartsWith("openid.", StringComparison.Ordinal) && pair.Key != "openid.mode")
                .Append(new KeyValuePair<string, string>("openid.mode", "check_authentication"));
            using var client = clients.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(check);
            using var answer = await client.PostAsync(OpenIdEndpoint, content, cancellationToken);
            string body = await answer.Content.ReadAsStringAsync(cancellationToken);
            return answer.IsSuccessStatusCode && body.Split('\n').Any(line => line.Trim() == "is_valid:true") ? steamId : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            LogLookupFailed(logger, "sign-in", error);
            return null;
        }
    }

    /// <summary>A SteamID64, or a custom profile name to resolve, from what someone pasted.</summary>
    internal static (string? SteamId, string? Vanity) Parse(string input)
    {
        string value = input.Trim().TrimEnd('/');
        if (SteamIds.IsSteamId64(value))
            return (value, null);
        if (Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out var uri)
            && uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) || uri?.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            var segments = uri!.AbsolutePath.Trim('/').Split('/');
            if (segments is ["profiles", var id, ..])
                return SteamIds.IsSteamId64(id) ? (id, null) : (null, null);
            if (segments is ["id", var name, ..] && VanityName().IsMatch(name))
                return (null, name);
            return (null, null);
        }
        return VanityName().IsMatch(value) ? (null, value) : (null, null);
    }

    internal static SteamLibrary ParseLibrary(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
            return new SteamLibrary(SteamLibraryStatus.Private, []); // Steam answers {} for private libraries
        var list = new List<SteamOwnedGame>();
        foreach (var game in games.EnumerateArray())
        {
            if (!game.TryGetProperty("appid", out var appId) || !game.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } title)
                continue;
            int Minutes(string property) => game.TryGetProperty(property, out var value) && value.TryGetInt32(out int minutes) ? minutes : 0;
            list.Add(new SteamOwnedGame(appId.GetInt64().ToString(CultureInfo.InvariantCulture), title.Length > 80 ? title[..80] : title,
                Minutes("playtime_forever"), Minutes("playtime_2weeks")));
        }
        return new SteamLibrary(SteamLibraryStatus.Ok, list);
    }

    /// <summary>Adds recently played games the owned list left out; games already listed keep their owned-list entry.</summary>
    internal static SteamLibrary WithRecent(SteamLibrary owned, SteamLibrary recent)
    {
        if (recent.Status != SteamLibraryStatus.Ok)
            return owned;
        var known = owned.Games.Select(game => game.AppId).ToHashSet(StringComparer.Ordinal);
        var missing = recent.Games.Where(game => known.Add(game.AppId)).ToArray();
        return missing.Length == 0 ? owned : owned with { Games = [.. owned.Games, .. missing] };
    }

    internal static string? ClaimedId(string claimed) =>
        claimed.StartsWith(IdentityPrefix, StringComparison.Ordinal) && SteamIds.IsSteamId64(claimed[IdentityPrefix.Length..])
            ? claimed[IdentityPrefix.Length..] : null;

    /// <summary>Steam echoes the return address with its own query values; compare the address up to its query.</summary>
    private static bool SameReturn(string actual, string expected) =>
        string.Equals(actual.Split('?')[0], expected.Split('?')[0], StringComparison.Ordinal);

    [GeneratedRegex("<steamID64>(7656119\\d{10})</steamID64>")]
    private static partial Regex SteamId64Tag();

    [GeneratedRegex("^[A-Za-z0-9_-]{2,32}$")]
    private static partial Regex VanityName();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steam {Lookup} lookup failed")]
    private static partial void LogLookupFailed(ILogger logger, string lookup, Exception error);
}
