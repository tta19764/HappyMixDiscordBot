using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Infrastructure.Persistence;

namespace HappyMix.Bot.Services;

public sealed partial class GameNewsService(GameNewsStore store, DiscordSocketClient discord, IHttpClientFactory clients,
    ILogger<GameNewsService> logger, NotificationChannels notificationChannels) : IDisposable
{
    private readonly SemaphoreSlim _changes = new(1);

    public async Task<GameNewsSubscription[]> ListAsync(ulong guildId, CancellationToken ct = default) =>
        (await store.ListAsync(ct)).Where(item => item.GuildId == guildId.ToString(System.Globalization.CultureInfo.InvariantCulture)).OrderBy(item => item.Name).ToArray();

    public async Task ChangeAsync(IGuild guild, IGuildUser member, uint appId, string action, string? name = null,
        GameNewsSources sources = GameNewsSources.Official)
    {
        sources &= GameNewsSources.All;
        if (action is "create" or "sources" && sources == GameNewsSources.None)
            throw new InvalidOperationException("Pick at least one kind of news.");
        await _changes.WaitAsync();
        try
        {
            var existing = (await ListAsync(guild.Id)).FirstOrDefault(item => item.AppId == appId);
            if (action == "create")
            {
                if (existing is not null)
                    throw new InvalidOperationException("This game already has a subscription.");
                if (appId == 0 || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 65)
                    throw new InvalidOperationException("Enter a Steam App ID or store link and a game name of up to 65 characters.");
                await CreateAsync(guild, member, appId, name.Trim(), sources);
                return;
            }

            var item = existing ?? throw new InvalidOperationException("Subscription not found.");
            var roleId = ulong.Parse(item.RoleId, System.Globalization.CultureInfo.InvariantCulture);
            if (action == "delete")
            {
                if (item.OwnerId != member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) && !member.GuildPermissions.Administrator)
                    throw new InvalidOperationException("Only the subscription creator or a server administrator can delete it.");
                await store.MarkDeletingAsync(item, default);
                await DeleteResourcesAsync(await discord.Rest.GetGuildAsync(guild.Id), item);
                await store.RemoveAsync(item, default);
                return;
            }

            if (item.Deleting)
                throw new InvalidOperationException("This subscription is being deleted.");

            if (action == "sources")
            {
                if (item.OwnerId != member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) && !member.GuildPermissions.Administrator)
                    throw new InvalidOperationException("Only the subscription creator or a server administrator can choose its news.");
                await SetSourcesAsync(item, sources);
                return;
            }

            if (action == "subscribe")
                await member.AddRoleAsync(roleId);
            else if (action == "unsubscribe")
                await member.RemoveRoleAsync(roleId);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>
    /// Saves the kinds of news to post. Posts of a newly ticked kind that are already out count as delivered,
    /// so turning on press articles doesn't flood the channel with months of old ones.
    /// </summary>
    private async Task SetSourcesAsync(GameNewsSubscription item, GameNewsSources sources)
    {
        var added = sources & ~item.Sources;
        if (added != GameNewsSources.None)
        {
            NewsItem[] posts;
            try
            {
                using var client = clients.CreateClient("SteamNews");
                posts = await ReadNewsAsync(client, item.AppId, item.CreatedAt, default);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                throw new InvalidOperationException("Steam didn't answer. Try again in a minute.", error);
            }
            foreach (var post in posts.Where(post => (Kind(post) & added) != 0))
                await store.RecordDeliveryAsync(item, post.Gid, default);
        }
        await store.SetSourcesAsync(item, sources, default);
        ulong guildId = ulong.Parse(item.GuildId, System.Globalization.CultureInfo.InvariantCulture);
        LogSourcesChanged(logger, guildId, item.Name, sources);
    }

    private async Task CreateAsync(IGuild guild, IGuildUser member, uint appId, string name, GameNewsSources sources)
    {
        IRole? role = null;
        ITextChannel? channel = null;
        try
        {
            role = await guild.CreateRoleAsync($"News · {name}", GuildPermissions.None, isMentionable: false);
            channel = await notificationChannels.CreateAsync(guild, $"{ChannelName(name)}-{appId}",
                $"Steam news for {name} · https://store.steampowered.com/app/{appId}/");
            var bot = await guild.GetCurrentUserAsync(CacheMode.AllowDownload);
            var permissions = bot.GetPermissions(channel);
            if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks || !permissions.MentionEveryone)
                throw new InvalidOperationException("HappyMix needs View Channel, Send Messages, Embed Links, and Mention Everyone in the news channel.");
            await member.AddRoleAsync(role.Id);
            await store.AddAsync(new(guild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), appId, name, member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), role.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Sources: sources), default);
        }
        catch
        {
            try
            {
                if (channel is not null) await channel.DeleteAsync();
            }
            finally
            {
                if (role is not null) await role.DeleteAsync();
            }
            throw;
        }
    }

    private static async Task DeleteResourcesAsync(IGuild guild, GameNewsSubscription item)
    {
        try
        {
            if (await guild.GetTextChannelAsync(ulong.Parse(item.ChannelId, System.Globalization.CultureInfo.InvariantCulture)) is { } channel)
                await channel.DeleteAsync();
        }
        catch (HttpException error) when (error.HttpCode == HttpStatusCode.NotFound) { }

        try
        {
            if (guild.GetRole(ulong.Parse(item.RoleId, System.Globalization.CultureInfo.InvariantCulture)) is { } role)
                await role.DeleteAsync();
        }
        catch (HttpException error) when (error.HttpCode == HttpStatusCode.NotFound) { }
    }

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

    private static string ChannelName(string name)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "News for {Game} in server {GuildId} now posts {Sources}")]
    private static partial void LogSourcesChanged(ILogger logger, ulong guildId, string game, GameNewsSources sources);

    public void Dispose() => _changes.Dispose();

    private sealed record NewsResponse([property: JsonPropertyName("appnews")] AppNews? AppNews);
    private sealed record AppNews([property: JsonPropertyName("newsitems")] NewsItem[] Items);
    internal sealed record NewsItem(string Gid, string Title, string Url, long Date,
        [property: JsonPropertyName("feedname")] string? FeedName = null,
        [property: JsonPropertyName("feedlabel")] string? FeedLabel = null,
        [property: JsonPropertyName("feed_type")] int? FeedType = null,
        [property: JsonPropertyName("tags")] string[]? Tags = null);
}
