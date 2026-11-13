using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HappyMix.Domain.Esports;
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

    public async Task<IReadOnlyList<string>> GetLiveAsync(string[] handles, Esport game, CancellationToken cancellationToken)
    {
        string? clientId = config["Twitch:ClientId"];
        string? secret = config["Twitch:ClientSecret"];
        if (handles.Length == 0 || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            return [];

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
            var live = (body?.Data ?? []).Where(stream => stream.Type == "live")
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
        Message = "Twitch live status lookup failed ({FailureType}); sending match start without links.")]
    private static partial void LogLookupFailed(ILogger logger, string failureType);

    private sealed record TokenResponse(string Access_Token, int Expires_In);
    private sealed record StreamResponse(Stream[] Data);
    private sealed record Stream(string User_Login, string Type);
}
