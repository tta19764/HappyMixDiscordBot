using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace HappyMix.Bot.Services;

/// <summary>
/// Steam game pictures. Older games have their header at a fixed address (<see cref="Header"/>); games released from
/// about 2025 only have it under a per-game path that Steam's store data names. Pages try the fixed address first and,
/// when it fails, ask <c>/steam-art/{appId}</c>, which looks the real address up here once and remembers it.
/// </summary>
public sealed partial class SteamArt(IHttpClientFactory clients, ILogger<SteamArt> logger)
{
    public const string HttpClientName = "SteamArt";
    private const int MaxCached = 5000;
    private static readonly TimeSpan FoundFor = TimeSpan.FromDays(1), MissingFor = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, (string? Url, DateTimeOffset Until)> cache = new(StringComparer.Ordinal);

    /// <summary>The fixed header address, which works for most games released before 2025.</summary>
    public static string Header(string appId) => $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

    public static string Fallback(string appId) => $"/steam-art/{appId}";

    /// <summary>The header picture Steam's store lists for the game, or null when it has none (tools, removed games).</summary>
    public async Task<string?> HeaderAsync(string appId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(appId, out var saved) && saved.Until > DateTimeOffset.UtcNow)
            return saved.Url;
        string? url = null;
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            using var answer = await client.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", cancellationToken);
            if (!answer.IsSuccessStatusCode)
                return null; // Rate limited or down: try again next time rather than remembering "no picture".
            using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellationToken));
            url = Parse(document.RootElement, appId);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogLookupFailed(logger, appId, error);
            return null;
        }
        if (cache.Count >= MaxCached)
            cache.Clear();
        cache[appId] = (url, DateTimeOffset.UtcNow + (url is null ? MissingFor : FoundFor));
        return url;
    }

    /// <summary>
    /// <c>header_image</c> from an appdetails answer, when it is an https address on Steam's own image servers.
    /// </summary>
    internal static string? Parse(JsonElement root, string appId) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(appId, out var app)
        && app.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
        && app.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("header_image", out var header) && header.GetString() is { } value
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".steampowered.com", StringComparison.OrdinalIgnoreCase))
            ? uri.ToString() : null;

    public static bool IsAppId(string value) => value.Length is > 0 and <= 10 && value.All(char.IsAsciiDigit)
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steam picture lookup for app {AppId} failed")]
    private static partial void LogLookupFailed(ILogger logger, string appId, Exception error);
}
