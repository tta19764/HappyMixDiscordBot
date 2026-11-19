using System.Globalization;
using System.Text;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.Polls;
using HappyMix.Application.Abstractions.Watch;
using HappyMix.Application.Watch;
using Microsoft.Extensions.Options;
using Poll = HappyMix.Application.Abstractions.Polls.Poll;

namespace HappyMix.Bot.Services;

/// <summary>
/// Votes in Discord: one message per vote with the options (and, for games, series, films and anime, a card with the
/// poster and details), a menu to vote, and a button to take the vote back. The message is updated after every vote
/// and shows the winner once voting ends.
/// </summary>
public sealed partial class PollMessages(DiscordSocketClient discord, IPollStore polls, IMediaCatalog catalog, IWatchStore watchlist,
    EventNotificationSettings notifications, IOptions<CommunityDefaults> community, IConfiguration configuration, IClock clock,
    ILogger<PollMessages> logger)
{
    public const string PickPrefix = "poll:pick";
    public const string ClearPrefix = "poll:clear";
    private const int MaxCards = 9;

    /// <summary>Posts the vote if it has a channel and no message yet, or updates the message it has.</summary>
    public async Task SyncAsync(Guid pollId, ulong guildId, CancellationToken cancellationToken = default)
    {
        if (await polls.GetAsync(guildId, pollId) is { ChannelId: not null } poll)
            await SyncAsync(poll, cancellationToken);
    }

    public async Task SyncAsync(Poll poll, CancellationToken cancellationToken = default)
    {
        if (poll.ChannelId is not { } channelId || discord.GetGuild(poll.GuildId)?.GetTextChannel(channelId) is not { } channel)
            return;
        bool closed = poll.IsClosed(clock.UtcNow);
        if (poll.MessageId is null && closed)
            return;
        try
        {
            var (content, embeds, components) = await BuildAsync(poll, cancellationToken);
            var options = new RequestOptions { CancelToken = cancellationToken };
            if (poll.MessageId is { } messageId)
            {
                await channel.ModifyMessageAsync(messageId, message =>
                {
                    message.Content = content;
                    message.Embeds = embeds;
                    message.Components = components;
                    message.AllowedMentions = AllowedMentions.None;
                }, options);
                await polls.SetMessageAsync(poll.Id, channelId, messageId, closed);
            }
            else
            {
                var mentions = poll.Audience.Count is > 0 and <= 25 ? new AllowedMentions { UserIds = poll.Audience.ToList() } : AllowedMentions.None;
                var message = await channel.SendMessageAsync(content, embeds: embeds, components: components, allowedMentions: mentions, options: options);
                await polls.SetMessageAsync(poll.Id, channelId, message.Id, false);
            }
        }
        catch (Discord.Net.HttpException error) when (error.HttpCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            // The message or channel is gone, or the bot lost access: stop trying and keep the vote on the dashboard.
            LogPostFailed(logger, poll.Id, error.HttpCode);
            await polls.SetMessageAsync(poll.Id, channelId, poll.MessageId, true);
        }
    }

    /// <summary>The channel a group's votes go to: its notification channel, when notifications are on.</summary>
    public async Task<ulong?> GroupChannelAsync(ulong guildId, Guid groupId) =>
        await notifications.GetEffectiveAsync(guildId, "group", groupId) is { Route: { Enabled: true } route } && route.Channel() is > 0 and var channel
            ? channel : null;

    private async Task<(string? Content, Embed[] Embeds, MessageComponent Components)> BuildAsync(Poll poll, CancellationToken cancellationToken)
    {
        var media = new Dictionary<MediaRef, MediaInfo>();
        foreach (var reference in poll.Options.Select(option => option.Media).OfType<MediaRef>().Distinct())
            if (await catalog.GetAsync(reference, cancellationToken) is { } info)
                media[reference] = info;
        var progress = poll.GroupId is { } groupId
            ? (await watchlist.ListAsync(poll.GuildId, groupId)).Where(item => item.Source is not null && item.ExternalId is not null)
                .ToDictionary(item => new MediaRef(item.Source!, item.ExternalId!))
            : [];
        string language = poll.GroupId is { } group && await notifications.GetEffectiveAsync(poll.GuildId, "group", group) is { Source: not NotificationRouteSource.Default } route
            ? route.Route.Language : community.Value.LanguageCode;
        return Build(poll, media, progress, language, clock.UtcNow, DashboardUrl(poll));
    }

    internal static (string? Content, Embed[] Embeds, MessageComponent Components) Build(Poll poll, IReadOnlyDictionary<MediaRef, MediaInfo> media,
        IReadOnlyDictionary<MediaRef, WatchItem> progress, string language, DateTimeOffset now, string? dashboardUrl)
    {
        string T(string key, params object[] values) => NotificationText.Get(language, key, values);
        bool closed = poll.IsClosed(now);
        int voters = poll.Votes.Select(vote => vote.UserId).Distinct().Count();
        int max = Math.Max(1, poll.Options.Max(option => poll.Count(option.Position)));
        var lines = new StringBuilder();
        foreach (var option in poll.Options)
        {
            int count = poll.Count(option.Position);
            int filled = (int)Math.Round(count * 8.0 / max);
            string label = option.Url is { } url ? $"[{Format.Sanitize(option.Label)}]({url})" : Format.Sanitize(option.Label);
            lines.Append(CultureInfo.InvariantCulture, $"`{option.Position + 1}` {Icon(option.Kind)} **{label}**\n")
                .Append(CultureInfo.InvariantCulture, $"{new string('▰', filled)}{new string('▱', 8 - filled)} {T("Polls_VoteCount", count)}\n");
        }
        var leader = poll.Leader;
        string footer = closed
            ? leader is null ? T("Polls_NoVotes") : T("Polls_WinnerLine", Format.Sanitize(leader.Label), poll.Count(leader.Position), voters)
            : T("Polls_EndsAt", $"<t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>") + " · " + T(poll.Multiple ? "Polls_PickSeveral" : "Polls_PickOne");
        var header = new EmbedBuilder()
            .WithTitle(Trim((closed ? "🏁 " : "🗳️ ") + poll.Question, 256))
            .WithDescription(Trim(lines.ToString() + "\n" + footer, 4096))
            .WithColor(closed ? new Color(0xF1C40F) : new Color(0x5865F2))
            .WithFooter(T("Polls_Voters", voters));

        var embeds = new List<Embed> { header.Build() };
        foreach (var option in poll.Options.Where(option => option.Media is not null && media.ContainsKey(option.Media)).Take(MaxCards))
        {
            var info = media[option.Media!];
            var card = new EmbedBuilder()
                .WithTitle(Trim($"{option.Position + 1}. {info.Title}", 256))
                .WithColor(KindColor(option.Kind, closed && leader?.Position == option.Position))
                .WithDescription(Trim(Details(info, language) + (info.Summary is { } summary ? "\n" + Trim(summary, 180) : "")
                    + (progress.GetValueOrDefault(option.Media!) is { } tracked ? "\n" + ProgressLine(tracked, language) : ""), 4096));
            if (info.Url is { } link) card.WithUrl(link);
            if (info.ImageUrl is { } image) card.WithThumbnailUrl(image);
            embeds.Add(card.Build());
        }

        var components = new ComponentBuilder();
        if (!closed)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"{PickPrefix}:{poll.GuildId}:{poll.Id:N}")
                .WithPlaceholder(T(poll.Multiple ? "Polls_PickSeveral" : "Polls_PickOne"))
                .WithMinValues(1)
                .WithMaxValues(poll.Multiple ? poll.Options.Count : 1);
            foreach (var option in poll.Options)
            {
                string? description = option.Media is { } reference && media.TryGetValue(reference, out var info) ? Trim(Details(info, language), 100) : null;
                menu.AddOption(Trim($"{option.Position + 1}. {option.Label}", 100), option.Position.ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrEmpty(description) ? null : description, option.Kind == "custom" ? null : new Emoji(Icon(option.Kind)));
            }
            components.WithSelectMenu(menu, 0)
                .WithButton(T("Polls_ClearVote"), $"{ClearPrefix}:{poll.GuildId}:{poll.Id:N}", ButtonStyle.Secondary, row: 1);
        }
        if (dashboardUrl is not null)
            components.WithButton(T("Polls_OpenDashboard"), style: ButtonStyle.Link, url: dashboardUrl, row: 1);
        return (null, embeds.ToArray(), components.Build());
    }

    /// <summary>
    /// The preset line for an option: what matters for its kind, e.g. "2016 · Drama, Sci-Fi · ★ 8.2 · 3 seasons, 28
    /// episodes · Ended" for a series or "2024 · 166 min · ★ 8.2" for a film.
    /// </summary>
    public static string Details(MediaInfo info, string language)
    {
        string T(string key, params object[] values) => NotificationText.Get(language, key, values);
        var parts = new List<string>();
        if (info.Year is { } year) parts.Add(year.ToString(CultureInfo.InvariantCulture));
        switch (info.Kind)
        {
            case "series" when info.Seasons.Count > 0:
                parts.Add(T("Media_SeasonsEpisodes", info.Seasons.Count, info.EpisodeCount ?? 0));
                break;
            case "anime" when info.EpisodeCount is { } episodes:
                parts.Add(T("Media_Episodes", episodes));
                break;
            case "film" when info.Runtime is { } runtime:
                parts.Add(T("Media_Minutes", runtime));
                break;
        }
        if (info.Genres.Count > 0) parts.Add(string.Join(", ", info.Genres.Take(3)));
        if (info.Rating is { } rating) parts.Add("★ " + rating.ToString("0.0", CultureInfo.InvariantCulture));
        if (info.Kind is "series" or "anime" && info.Status is { } status) parts.Add(status);
        return string.Join(" · ", parts);
    }

    public static string ProgressLine(WatchItem item, string language)
    {
        string T(string key, params object[] values) => NotificationText.Get(language, key, values);
        if (item.Status == WatchStatus.Watched) return "✅ " + T("Watch_Watched");
        string next = WatchKinds.HasEpisodes(item.Kind) ? "📍 " + T("Watch_NextEpisode", item.Season, item.Episode) : "📍 " + T("Watch_InProgress");
        return item.PositionSeconds is { } seconds ? next + " · " + T("Watch_StoppedAt", WatchKinds.FormatPosition(seconds)) : next;
    }

    internal static string Icon(string kind) => kind switch { "game" => "🎮", "series" => "📺", "film" => "🎬", "anime" => "🌸", _ => "▫️" };

    private static Color KindColor(string kind, bool winner) => winner ? new Color(0xF1C40F) : kind switch
    {
        "game" => new Color(0x57F287), "series" => new Color(0x3498DB), "film" => new Color(0xE74C3C), "anime" => new Color(0xEB459E), _ => new Color(0x99AAB5)
    };

    private string? DashboardUrl(Poll poll) =>
        configuration.GetValue<bool>("Dashboard:Enabled") && Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
            ? new Uri(origin, poll.GroupId is { } group ? $"/groups/{poll.GuildId}/{group}#votes" : $"/votes/{poll.GuildId}#votes").ToString()
            : null;

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vote {PollId} couldn't be posted or updated in Discord ({Status}); it stays on the dashboard")]
    private static partial void LogPostFailed(ILogger logger, Guid pollId, System.Net.HttpStatusCode? status);
}
