using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.News;

namespace HappyMix.Bot.Services;

/// <summary>
/// Posts new Steam news to subscription channels (run by <see cref="Jobs.PublishGameNewsJob"/>). Creating, joining and
/// deleting subscriptions go through the game news commands; <see cref="DiscordGameNewsGateway"/> does their Discord side.
/// </summary>
public sealed partial class GameNewsService(IGameNewsStore store, DiscordSocketClient discord, IHttpClientFactory clients,
    ILogger<GameNewsService> logger)
{
    public async Task PublishAsync(CancellationToken ct)
    {
        if (discord.ConnectionState != ConnectionState.Connected)
            return;
        using var client = clients.CreateClient("SteamNews");
        var subscriptions = await store.ListAsync(ct);
        foreach (var group in subscriptions.Where(item => !item.Deleting).GroupBy(item => item.AppId))
        {
            try
            {
                var posts = await ReadNewsAsync(client, group.Key, group.Min(item => item.CreatedAt), ct);
                foreach (var subscription in group)
                {
                    try { await PublishSubscriptionAsync(subscription, posts, ct); }
                    catch (Exception error) when (!ct.IsCancellationRequested)
                    {
                        LogPublishFailure(logger, subscription.GuildId, error);
                    }
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                LogFeedFailure(logger, group.Key, error);
            }
        }
    }

    /// <summary>The feed of the developer's own posts on the game's Steam page; every other feed is a press site.</summary>
    internal const string OfficialFeed = "steam_community_announcements";

    /// <summary>Patch notes and announcements come from the Steam page (patch notes are tagged); anything else is press.</summary>
    internal static GameNewsSources Kind(NewsItem post) =>
        post.FeedName == OfficialFeed || post.FeedType == 1
            ? post.Tags?.Contains("patchnotes", StringComparer.OrdinalIgnoreCase) == true ? GameNewsSources.PatchNotes : GameNewsSources.Announcements
            : GameNewsSources.Press;

    internal static async Task<NewsItem[]> ReadNewsAsync(HttpClient client, uint appId, long since, CancellationToken ct)
    {
        var posts = new Dictionary<string, NewsItem>(StringComparer.Ordinal);
        long? before = null;
        while (true)
        {
            string endpoint = FormattableString.Invariant(
                $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=100&maxlength=400");
            if (before is { } cursor) endpoint += FormattableString.Invariant($"&enddate={cursor}");
            var result = await client.GetFromJsonAsync<NewsResponse>(endpoint, ct);
            var page = result?.AppNews?.Items ?? [];
            foreach (var post in page.Where(post => post.Date > since)) posts.TryAdd(post.Gid, post);
            if (page.Length < 100 || page.Min(post => post.Date) <= since) break;
            long oldest = page.Min(post => post.Date);
            if (before is { } previous && oldest >= previous)
                throw new InvalidOperationException("Steam news pagination did not advance.");
            before = oldest;
        }
        return posts.Values.OrderBy(post => post.Date).ToArray();
    }

    private async Task PublishSubscriptionAsync(GameNewsSubscription item, NewsItem[] posts, CancellationToken ct)
    {
        var channelId = ulong.Parse(item.ChannelId, System.Globalization.CultureInfo.InvariantCulture);
        var roleId = ulong.Parse(item.RoleId, System.Globalization.CultureInfo.InvariantCulture);
        if (discord.GetChannel(channelId) is not SocketTextChannel channel)
            return;
        var delivered = await store.DeliveredAsync(item, ct);
        foreach (var post in posts.OrderBy(post => post.Date))
        {
            var kind = Kind(post);
            if (post.Date <= item.CreatedAt || (kind & item.Sources) == 0 || delivered.Contains(post.Gid) ||
                !Uri.TryCreate(post.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                continue;

            string heading = kind switch
            {
                GameNewsSources.PatchNotes => "Patch notes",
                GameNewsSources.Press => string.IsNullOrWhiteSpace(post.FeedLabel ?? post.FeedName) ? "Press" : $"Press · {post.FeedLabel ?? post.FeedName}",
                _ => Classify(post.Title)
            };
            string description = post.Title.Length > 256 ? post.Title[..256] : post.Title;
            var embed = new EmbedBuilder().WithTitle($"{item.Name} · {heading}").WithDescription(description)
                .WithUrl(url.AbsoluteUri).WithColor(Color.Blue).Build();
            await channel.SendMessageAsync($"<@&{roleId}>", embed: embed,
                allowedMentions: new AllowedMentions { RoleIds = [roleId] },
                options: new RequestOptions { CancelToken = ct });
            await store.RecordDeliveryAsync(item, post.Gid, ct);
        }
    }

    internal static string Classify(string title)
    {
        if (title.Contains("open beta", StringComparison.OrdinalIgnoreCase) || title.Contains("playtest", StringComparison.OrdinalIgnoreCase))
            return "Beta / playtest news";
        if (title.Contains("out now", StringComparison.OrdinalIgnoreCase) || title.Contains("now available", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("has been released", StringComparison.OrdinalIgnoreCase))
            return "Release announcement";
        if (title.Contains("patch", StringComparison.OrdinalIgnoreCase) || title.Contains("update", StringComparison.OrdinalIgnoreCase))
            return "Game update";
        return "Steam news";
    }

    internal static string ChannelName(string name)
    {
        string slug = string.Concat(name.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-')).Trim('-');
        return slug.Length == 0 ? "game-news" : slug;
    }

    public static uint ParseAppId(string? input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            input = parts is ["app", _, ..] ? parts[1] : null;
        }

        return uint.TryParse(input, System.Globalization.CultureInfo.InvariantCulture, out var appId) ? appId : 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish Steam news in server {GuildId}")]
    private static partial void LogPublishFailure(ILogger logger, string guildId, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read Steam news for app {AppId}")]
    private static partial void LogFeedFailure(ILogger logger, uint appId, Exception error);

    private sealed record NewsResponse([property: JsonPropertyName("appnews")] AppNews? AppNews);
    private sealed record AppNews([property: JsonPropertyName("newsitems")] NewsItem[] Items);
    internal sealed record NewsItem(string Gid, string Title, string Url, long Date,
        [property: JsonPropertyName("feedname")] string? FeedName = null,
        [property: JsonPropertyName("feedlabel")] string? FeedLabel = null,
        [property: JsonPropertyName("feed_type")] int? FeedType = null,
        [property: JsonPropertyName("tags")] string[]? Tags = null);
}
