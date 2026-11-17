using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HappyMix.Domain.Esports;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HappyMix.Bot.Services;

/// <summary>Checks configured channels against Twitch Helix at announcement time.</summary>
internal sealed partial class TwitchLiveStreams(
    IHttpClientFactory clients, IConfiguration config, ILogger<TwitchLiveStreams> logger) : IDisposable
{
    private string? _token;
    private DateTimeOffset _expiresAt;
    private readonly SemaphoreSlim _tokenLock = new(1);

    /// <summary>
    /// Channels from <paramref name="handles"/> that are live in the game's category. With <paramref name="teams"/>, only
    /// channels whose stream title names every known team (see <see cref="StreamTitleMatcher"/>) are returned.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLiveAsync(string[] handles, Esport game, CancellationToken cancellationToken,
        IReadOnlyCollection<EsportsOpponent>? teams = null)
    {
        string? clientId = config["Twitch:ClientId"];
        string? secret = config["Twitch:ClientSecret"];
        if (handles.Length == 0 || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            return [];

        if (handles.Length > 100)
        {
            var liveChannels = new List<string>();
            foreach (var batch in handles.Chunk(100))
                liveChannels.AddRange(await GetLiveAsync(batch, game, cancellationToken, teams));
            return liveChannels;
        }

        using var client = clients.CreateClient("Twitch");
        try
        {
            var token = await GetTokenAsync(client, clientId, secret, cancellationToken);
            string query = string.Join("&", handles.Select(handle => $"user_login={Uri.EscapeDataString(handle)}"));
            string category = game == Esport.CounterStrike2 ? "32399" : "29595";
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.twitch.tv/helix/streams?first=100&game_id={category}&{query}");
            request.Headers.Add("Client-Id", clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                await InvalidateTokenAsync(token, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<StreamResponse>(cancellationToken);
            var matcher = teams is { Count: > 0 } ? new StreamTitleMatcher(teams) : null;
            var live = (body?.Data ?? []).Where(stream => stream.Type == "live" && (matcher?.Matches(stream.Title) ?? true))
                .Select(stream => stream.User_Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return handles.Where(live.Contains).ToArray();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            LogLookupFailed(logger, exception.GetType().Name);
            return [];
        }
    }

    public async Task<string[]> DiscoverAsync(Esport game, IReadOnlyCollection<EsportsOpponent> teams,
        StreamDiscoveryPreferences? preferences, CancellationToken ct)
    {
        if (preferences is not { Enabled: true } || teams.Count < 2) return [];
        string? clientId = config["Twitch:ClientId"], secret = config["Twitch:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)) return [];
        using var client = clients.CreateClient("Twitch");
        try
        {
            var token = await GetTokenAsync(client, clientId, secret, ct);
            var found = new List<Stream>();
            string? cursor = null;
            var matcher = new StreamTitleMatcher(teams);
            int pages = Math.Clamp(config.GetValue("Twitch:DiscoveryMaxPages", 10), 1, 100);
            for (int page = 0; page < pages; page++)
            {
                string category = game == Esport.CounterStrike2 ? "32399" : "29595";
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.twitch.tv/helix/streams?first=100&game_id={category}" +
                    (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor)));
                request.Headers.Add("Client-Id", clientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized) await InvalidateTokenAsync(token, ct);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<StreamResponse>(ct);
                found.AddRange((body?.Data ?? []).Where(stream => stream.Type == "live" && matcher.Matches(stream.Title)));
                string? next = body?.Pagination?.Cursor;
                if (string.IsNullOrEmpty(next) || next == cursor) break;
                cursor = next;
            }
            return Rank(found, preferences).Select(stream => stream.User_Login).Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(config.GetValue("Twitch:DiscoveryMaxResults", 5), 1, 10)).ToArray();
        }
        catch (Exception exception) when (!ct.IsCancellationRequested &&
            exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            LogLookupFailed(logger, exception.GetType().Name);
            return [];
        }
    }

    internal static IOrderedEnumerable<Stream> Rank(IEnumerable<Stream> streams, StreamDiscoveryPreferences preferences)
    {
        IOrderedEnumerable<Stream> ordered = streams.OrderBy(_ => 0);
        foreach (string criterion in StreamDiscoveryPreferences.Split(preferences.Priority))
        {
            ordered = criterion switch
            {
                "language" => ordered.ThenBy(stream => PreferenceIndex(preferences.Languages, [stream.Language])),
                "country" => ordered.ThenBy(stream => PreferenceIndex(preferences.Countries, stream.Tags ?? [])),
                "tags" => ordered.ThenBy(stream => PreferenceIndex(preferences.Tags, stream.Tags ?? [])),
                "viewers" => ordered.ThenByDescending(stream => stream.Viewer_Count),
                _ => ordered
            };
        }
        return ordered.ThenBy(stream => stream.User_Login, StringComparer.OrdinalIgnoreCase);
    }

    private static int PreferenceIndex(string preferences, string[] values)
    {
        var ordered = StreamDiscoveryPreferences.Split(preferences);
        int index = Array.FindIndex(ordered, value => values.Contains(value, StringComparer.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private async Task<string> GetTokenAsync(HttpClient client, string clientId, string secret, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _token;

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["grant_type"] = "client_credentials"
            });
            using var response = await client.PostAsync("https://id.twitch.tv/oauth2/token", content, ct);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
            if (string.IsNullOrWhiteSpace(token?.Access_Token))
                throw new HttpRequestException("Twitch returned no access token.");

            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.Expires_In - 60));
            return _token = token.Access_Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task InvalidateTokenAsync(string token, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token == token)
                _token = null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void Dispose() => _tokenLock.Dispose();

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Twitch live status lookup failed ({FailureType}); sending announcement without links.")]
    private static partial void LogLookupFailed(ILogger logger, string failureType);

    private sealed record TokenResponse(string Access_Token, int Expires_In);
    private sealed record StreamResponse(Stream[] Data, Pagination? Pagination);
    private sealed record Pagination(string? Cursor);
    internal sealed record Stream(string User_Login, string Type, string? Title, string Language = "", string[]? Tags = null, int Viewer_Count = 0);
}
