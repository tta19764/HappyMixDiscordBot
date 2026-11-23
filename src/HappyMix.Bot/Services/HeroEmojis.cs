using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;

namespace HappyMix.Bot.Services;

/// <summary>
/// Dota 2 hero icons as the bot's own application emojis (named <c>d2h&lt;hero id&gt;</c>), for the meta posts. They
/// belong to the bot, not to a server, so no server emoji slots are used. Missing icons are uploaded the first time a
/// hero is posted; anything that fails simply leaves that hero without an icon.
/// </summary>
internal sealed partial class HeroEmojis(DiscordSocketClient discord, IHttpClientFactory clients, ILogger<HeroEmojis> logger) : IDisposable
{
    public const string HttpClientName = "HeroIcons";
    private const string Prefix = "d2h";
    private readonly SemaphoreSlim gate = new(1);
    private Dictionary<int, string>? known;

    public void Dispose() => gate.Dispose();

    /// <summary>Emoji text by hero id (<c>&lt;:d2h14:123…&gt;</c>) for the given heroes; heroes without an icon are left out.</summary>
    public async Task<IReadOnlyDictionary<int, string>> GetAsync(IEnumerable<(int HeroId, string? Image)> heroes, CancellationToken cancellationToken)
    {
        var wanted = heroes.Where(hero => hero.HeroId > 0).DistinctBy(hero => hero.HeroId).ToArray();
        if (wanted.Length == 0 || discord.ConnectionState != ConnectionState.Connected)
            return new Dictionary<int, string>();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (known is null)
            {
                known = [];
                foreach (var emote in await ((IDiscordClient)discord).GetApplicationEmotesAsync())
                    if (HeroId(emote.Name) is { } id)
                        known[id] = emote.ToString();
            }
            using var client = clients.CreateClient(HttpClientName);
            foreach (var (heroId, image) in wanted.Where(hero => !known.ContainsKey(hero.HeroId)))
            {
                if (IconUrl(image) is not { } url)
                    continue;
                try
                {
                    await using var icon = await client.GetStreamAsync(url, cancellationToken);
                    using var copy = new MemoryStream();
                    await icon.CopyToAsync(copy, cancellationToken);
                    copy.Position = 0;
                    var emote = await ((IDiscordClient)discord).CreateApplicationEmoteAsync(Prefix + heroId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        new Image(copy), new RequestOptions { CancelToken = cancellationToken });
                    known[heroId] = emote.ToString();
                }
                catch (Exception error) when (error is HttpRequestException or Discord.Net.HttpException or TaskCanceledException
                    && !cancellationToken.IsCancellationRequested)
                {
                    LogIconFailed(logger, heroId, error);
                }
            }
            return wanted.Where(hero => known.ContainsKey(hero.HeroId)).ToDictionary(hero => hero.HeroId, hero => known[hero.HeroId]);
        }
        catch (Discord.Net.HttpException error)
        {
            // Listing the emojis failed: post without icons and try again next time.
            known = null;
            LogIconFailed(logger, 0, error);
            return new Dictionary<int, string>();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The hero id of an emoji the bot made, or null for any other emoji.</summary>
    internal static int? HeroId(string? name) =>
        name is not null && HeroName().Match(name) is { Success: true } match ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The square 32×32 icon next to the hero's wide portrait on Steam's CDN (…/heroes/icons/lina.png).</summary>
    internal static string? IconUrl(string? portrait) =>
        portrait is not null && portrait.Contains("/dota_react/heroes/", StringComparison.Ordinal) && !portrait.Contains("/heroes/icons/", StringComparison.Ordinal)
            ? portrait.Replace("/dota_react/heroes/", "/dota_react/heroes/icons/", StringComparison.Ordinal)
            : null;

    [GeneratedRegex(@"^d2h(\d{1,4})$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HeroName();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dota 2 hero icon {HeroId} couldn't be made into an emoji")]
    private static partial void LogIconFailed(ILogger logger, int heroId, Exception error);
}
