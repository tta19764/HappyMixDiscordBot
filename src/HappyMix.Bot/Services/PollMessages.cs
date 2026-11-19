using System.Globalization;
using System.Text;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.Polls;
using HappyMix.Application.Abstractions.Watch;
using HappyMix.Application.Polls;
using MediatR;
using HappyMix.Application.Watch;
using Microsoft.Extensions.Options;
using Poll = HappyMix.Application.Abstractions.Polls.Poll;

namespace HappyMix.Bot.Services;

/// <summary>
/// Votes in Discord, in one of two forms.
/// <list type="bullet">
/// <item>A plain vote (text options, for a group or the whole server) is a Discord poll: people vote with Discord's own
/// poll controls, Discord shows the result, and the bot copies the votes so the dashboard stays in step.</item>
/// <item>A vote on games, series, films or anime with catalog details is a card message: each option shows its poster,
/// linked title, details and watch progress, with a menu to vote. Votes for chosen members use it too, because a Discord
/// poll can't limit who votes.</item>
/// </list>
/// </summary>
public sealed partial class PollMessages(DiscordSocketClient discord, IPollStore polls, IMediaCatalog catalog, IWatchStore watchlist,
    ISender sender, EventNotificationSettings notifications, IOptions<CommunityDefaults> community, IConfiguration configuration,
    IClock clock, ILogger<PollMessages> logger)
{
    public const string PickPrefix = "poll:pick";
    public const string ClearPrefix = "poll:clear";
    /// <summary>Discord's limits for polls: answer text length and longest duration in hours.</summary>
    public const int NativeAnswerLength = 55;
    public const int NativeMaxHours = 768;
    /// <summary>Discord allows at most 40 components in one message.</summary>
    internal const int ComponentLimit = 40;

    /// <summary>Whether a vote is posted as a Discord poll rather than a card message.</summary>
    public static bool UsesNativePoll(Poll poll) =>
        poll.Options.All(option => option.Media is null && option.Label.Length <= NativeAnswerLength)
        && (poll.Everyone || poll.GroupId is not null) && poll.Question.Length <= 300;

    /// <summary>Posts the vote if it has a channel and no message yet, or brings the message up to date.</summary>
    public async Task SyncAsync(Guid pollId, ulong guildId, CancellationToken cancellationToken = default)
    {
        if (await polls.GetAsync(guildId, pollId) is { ChannelId: not null } poll)
            await SyncAsync(poll, cancellationToken);
    }

    public async Task SyncAsync(Poll poll, CancellationToken cancellationToken = default)
    {
        if (poll.ChannelId is not { } channelId || discord.GetGuild(poll.GuildId)?.GetTextChannel(channelId) is not { } channel)
            return;
        var now = clock.UtcNow;
        bool closed = poll.IsClosed(now);
        if (poll.MessageId is null && closed)
            return;
        var options = new RequestOptions { CancelToken = cancellationToken };
        try
        {
            if (poll.MessageId is null)
                await PostAsync(poll, channel, now, options);
            else if (poll.Native)
                await SyncNativeAsync(poll, channel, poll.MessageId.Value, closed, options);
            else
                await UpdateCardsAsync(poll, channel, poll.MessageId.Value, closed, options, cancellationToken);
        }
        catch (Discord.Net.HttpException error) when (error.HttpCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            // The message or channel is gone, or the bot lost access: stop trying and keep the vote on the dashboard.
            LogPostFailed(logger, poll.Id, error.HttpCode);
            await polls.SetMessageAsync(poll.Id, channelId, poll.MessageId, true, poll.Native);
        }
    }

    /// <summary>A vote added or removed with Discord's poll controls: copied into the vote right away.</summary>
    public Task OnNativeVoteAsync(ulong guildId, ulong userId, ulong messageId, ulong answerId, bool added) =>
        sender.Send(new CopyDiscordPollVoteCommand(guildId, userId, messageId, (int)answerId - 1, added));

    /// <summary>Copies the current votes of an open Discord poll, catching up on anything missed while offline.</summary>
    public async Task ReconcileAsync(Poll poll, CancellationToken cancellationToken = default)
    {
        if (!poll.Native || poll.MessageId is not { } messageId || poll.ChannelId is not { } channelId ||
            discord.GetGuild(poll.GuildId)?.GetTextChannel(channelId) is not { } channel ||
            await channel.GetMessageAsync(messageId, options: new RequestOptions { CancelToken = cancellationToken }) is not IUserMessage message)
            return;
        await CopyVotesAsync(poll, message, cancellationToken);
    }

    /// <summary>The channel a group's votes go to: its notification channel, when notifications are on.</summary>
    public async Task<ulong?> GroupChannelAsync(ulong guildId, Guid groupId) =>
        await notifications.GetEffectiveAsync(guildId, "group", groupId) is { Route: { Enabled: true } route } && route.Channel() is > 0 and var channel
            ? channel : null;

    private async Task PostAsync(Poll poll, SocketTextChannel channel, DateTimeOffset now, RequestOptions options)
    {
        if (UsesNativePoll(poll))
        {
            var message = await channel.SendMessageAsync(poll: NativePoll(poll, now), allowedMentions: AllowedMentions.None, options: options);
            await polls.SetMessageAsync(poll.Id, channel.Id, message.Id, false, native: true);
            return;
        }
        string language = await LanguageAsync(poll);
        var (media, progress) = await DetailsAsync(poll, options.CancelToken);
        var mentions = poll.Audience.Count is > 0 and <= 25 ? new AllowedMentions { UserIds = poll.Audience.ToList() } : AllowedMentions.None;
        var sent = await channel.SendMessageAsync(components: BuildCards(poll, media, progress, language, now, DashboardUrl(poll)),
            flags: MessageFlags.ComponentsV2, allowedMentions: mentions, options: options);
        await polls.SetMessageAsync(poll.Id, channel.Id, sent.Id, false);
    }

    private async Task SyncNativeAsync(Poll poll, SocketTextChannel channel, ulong messageId, bool closed, RequestOptions options)
    {
        if (await channel.GetMessageAsync(messageId, options: options) is not IUserMessage message)
            return;
        if (closed)
        {
            // Ended early on the dashboard or with /vote end: end the Discord poll too, so Discord shows the result.
            if (message.Poll is { } live && live.Results?.IsFinalized != true && live.ExpiresAt > clock.UtcNow)
                await message.EndPollAsync(options);
            await CopyVotesAsync(poll, message, options.CancelToken);
            await polls.SetMessageAsync(poll.Id, channel.Id, messageId, true, native: true);
        }
        else
            await CopyVotesAsync(poll, message, options.CancelToken);
    }

    private async Task UpdateCardsAsync(Poll poll, SocketTextChannel channel, ulong messageId, bool closed, RequestOptions options,
        CancellationToken cancellationToken)
    {
        string language = await LanguageAsync(poll);
        var (media, progress) = await DetailsAsync(poll, cancellationToken);
        var cards = BuildCards(poll, media, progress, language, clock.UtcNow, DashboardUrl(poll));
        try
        {
            await channel.ModifyMessageAsync(messageId, message =>
            {
                message.Content = "";
                message.Embeds = Array.Empty<Embed>();
                message.Components = cards;
                message.Flags = MessageFlags.ComponentsV2;
                message.AllowedMentions = AllowedMentions.None;
            }, options);
        }
        catch (Discord.Net.HttpException error) when (error.HttpCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Votes posted by v0.0.7 use embeds, and Discord may refuse to turn them into the new layout: keep their format.
            var (embeds, components) = BuildEmbeds(poll, language, clock.UtcNow, DashboardUrl(poll));
            await channel.ModifyMessageAsync(messageId, message =>
            {
                message.Embeds = embeds;
                message.Components = components;
                message.AllowedMentions = AllowedMentions.None;
            }, options);
        }
        await polls.SetMessageAsync(poll.Id, channel.Id, messageId, closed);
    }

    /// <summary>Replaces the stored votes with the voters Discord lists for each answer.</summary>
    private async Task CopyVotesAsync(Poll poll, IUserMessage message, CancellationToken cancellationToken)
    {
        if (message.Poll is not { } live)
            return;
        var votes = new Dictionary<ulong, List<int>>();
        foreach (var answer in live.Answers)
        {
            int position = (int)answer.AnswerId - 1;
            if (poll.Options.All(option => option.Position != position))
                continue;
            await foreach (var page in message.GetPollAnswerVotersAsync(answer.AnswerId, 100, null, new RequestOptions { CancelToken = cancellationToken }))
                foreach (var user in page.Where(user => !user.IsBot))
                {
                    if (!votes.TryGetValue(user.Id, out var positions))
                        votes[user.Id] = positions = [];
                    if (!positions.Contains(position))
                        positions.Add(position);
                }
        }
        await sender.Send(new SyncDiscordPollVotesCommand(poll.GuildId, poll.Id, votes.ToDictionary(item => item.Key, item => item.Value.ToArray())), cancellationToken);
    }

    private async Task<(Dictionary<MediaRef, MediaInfo> Media, Dictionary<MediaRef, WatchItem> Progress)> DetailsAsync(Poll poll,
        CancellationToken cancellationToken)
    {
        var media = new Dictionary<MediaRef, MediaInfo>();
        foreach (var reference in poll.Options.Select(option => option.Media).OfType<MediaRef>().Distinct())
            if (await catalog.GetAsync(reference, cancellationToken) is { } info)
                media[reference] = info;
        var progress = poll.GroupId is { } groupId
            ? (await watchlist.ListAsync(poll.GuildId, groupId)).Where(item => item.Source is not null && item.ExternalId is not null)
                .ToDictionary(item => new MediaRef(item.Source!, item.ExternalId!))
            : [];
        return (media, progress);
    }

    private async Task<string> LanguageAsync(Poll poll) =>
        poll.GroupId is { } group && await notifications.GetEffectiveAsync(poll.GuildId, "group", group) is { Source: not NotificationRouteSource.Default } route
            ? route.Route.Language : community.Value.LanguageCode;

    /// <summary>A Discord poll for a plain vote. Discord counts in whole hours, up to 32 days.</summary>
    internal static PollProperties NativePoll(Poll poll, DateTimeOffset now) => new()
    {
        Question = new PollMediaProperties { Text = poll.Question },
        Answers = poll.Options.OrderBy(option => option.Position)
            .Select(option => new PollMediaProperties { Text = option.Label, Emoji = option.Kind == "custom" ? null : new Emoji(Icon(option.Kind)) })
            .ToList(),
        Duration = (uint)Math.Clamp(Math.Ceiling((poll.ClosesAt - now).TotalHours), 1, NativeMaxHours),
        AllowMultiselect = poll.Multiple,
        LayoutType = PollLayout.Default
    };

    /// <summary>
    /// The card message (Discord's component layout): a header, one section per option with its poster, linked title,
    /// details, watch progress and vote bar, and the vote menu. Once voting ends, the winner leads with a large poster.
    /// </summary>
    internal static MessageComponent BuildCards(Poll poll, IReadOnlyDictionary<MediaRef, MediaInfo> media,
        IReadOnlyDictionary<MediaRef, WatchItem> progress, string language, DateTimeOffset now, string? dashboardUrl)
    {
        string T(string key, params object[] values) => NotificationText.Get(language, key, values);
        bool closed = poll.IsClosed(now);
        int voters = poll.Votes.Select(vote => vote.UserId).Distinct().Count();
        int max = Math.Max(1, poll.Options.Max(option => poll.Count(option.Position)));
        var leader = poll.Leader;
        MediaInfo? Info(PollOption option) => option.Media is { } reference && media.TryGetValue(reference, out var info) ? info : null;

        // Fixed parts: container, header, two separators and footer, plus the menu row and button row below it.
        int below = closed ? (dashboardUrl is null ? 0 : 2) : 2 + 1 + 1 + (dashboardUrl is null ? 0 : 1);
        int used = 5 + below + poll.Options.Count; // every option needs at least one text component
        var container = new ContainerBuilder().WithAccentColor(closed ? new Color(0xF1C40F) : AccentFor(poll));
        string mentions = poll.Audience.Count is > 0 and <= 25 && !closed ? "\n" + string.Join(" ", poll.Audience.Select(id => $"<@{id}>")) : "";
        container.AddComponent(new TextDisplayBuilder(Trim($"## {(closed ? "🏁" : "🗳️")} {Format.Sanitize(poll.Question)}{mentions}", 1000)));
        if (closed && leader is not null && Info(leader)?.ImageUrl is { } winnerPoster && used + 2 <= ComponentLimit)
        {
            container.AddComponent(new MediaGalleryBuilder().AddItem(winnerPoster, leader.Label, false));
            used += 2;
        }
        container.AddComponent(new SeparatorBuilder(true, SeparatorSpacingSize.Small, null));

        bool summaries = poll.Options.Count <= 4;
        var ordered = closed ? poll.Options.OrderByDescending(option => poll.Count(option.Position)).ThenBy(option => option.Position) : poll.Options.AsEnumerable();
        foreach (var option in ordered)
        {
            var info = Info(option);
            int count = poll.Count(option.Position);
            int filled = (int)Math.Round(count * 10.0 / max);
            string title = option.Url is { } url ? $"[{Format.Sanitize(option.Label)}]({url})" : Format.Sanitize(option.Label);
            bool winner = closed && leader?.Position == option.Position;
            var text = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"### {(winner ? "🏆 " : "")}`{option.Position + 1}` {Icon(option.Kind)} {title}");
            if (info is not null)
            {
                text.Append("\n-# ").Append(Details(info, language));
                if (summaries && info.Summary is { } summary)
                    text.Append('\n').Append(Format.Sanitize(Trim(summary, 140)));
            }
            if (option.Media is { } reference && progress.TryGetValue(reference, out var tracked))
                text.Append('\n').Append(ProgressLine(tracked, language));
            text.Append('\n').Append(CultureInfo.InvariantCulture, $"`{new string('█', filled)}{new string('░', 10 - filled)}` **{count}**");

            // A poster needs a section and a thumbnail on top of the text: two more components, while they fit.
            if (info?.ImageUrl is { } poster && used + 2 <= ComponentLimit)
            {
                container.AddComponent(new SectionBuilder()
                    .AddComponent(new TextDisplayBuilder(text.ToString()))
                    .WithAccessory(new ThumbnailBuilder(new UnfurledMediaItemProperties(poster), Trim(info.Title, 256), false, null)));
                used += 2;
            }
            else
                container.AddComponent(new TextDisplayBuilder(text.ToString()));
        }

        container.AddComponent(new SeparatorBuilder(true, SeparatorSpacingSize.Small, null));
        string footer = closed
            ? leader is null ? T("Polls_NoVotes") : T("Polls_WinnerLine", Format.Sanitize(leader.Label), poll.Count(leader.Position), voters)
            : T("Polls_EndsAt", $"<t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>") + " · " + T(poll.Multiple ? "Polls_PickSeveral" : "Polls_PickOne")
              + " · " + T("Polls_Voters", voters);
        container.AddComponent(new TextDisplayBuilder("-# " + footer));

        var message = new ComponentBuilderV2().AddComponent(container);
        if (!closed)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"{PickPrefix}:{poll.GuildId}:{poll.Id:N}")
                .WithPlaceholder(T(poll.Multiple ? "Polls_PickSeveral" : "Polls_PickOne"))
                .WithMinValues(1)
                .WithMaxValues(poll.Multiple ? poll.Options.Count : 1);
            foreach (var option in poll.Options)
            {
                string? description = Info(option) is { } info ? Trim(Details(info, language), 100) : null;
                menu.AddOption(Trim($"{option.Position + 1}. {option.Label}", 100), option.Position.ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrEmpty(description) ? null : description, option.Kind == "custom" ? null : new Emoji(Icon(option.Kind)));
            }
            message.AddComponent(new ActionRowBuilder().WithSelectMenu(menu));
            var buttons = new ActionRowBuilder().WithButton(new ButtonBuilder(T("Polls_ClearVote"), $"{ClearPrefix}:{poll.GuildId}:{poll.Id:N}", ButtonStyle.Secondary));
            if (dashboardUrl is not null)
                buttons.WithButton(new ButtonBuilder().WithLabel(T("Polls_OpenDashboard")).WithStyle(ButtonStyle.Link).WithUrl(dashboardUrl));
            message.AddComponent(buttons);
        }
        else if (dashboardUrl is not null)
            message.AddComponent(new ActionRowBuilder().WithButton(new ButtonBuilder().WithLabel(T("Polls_OpenDashboard")).WithStyle(ButtonStyle.Link).WithUrl(dashboardUrl)));
        return message.Build();
    }

    /// <summary>The simple embed format of v0.0.7, used only to update messages Discord won't convert to the card layout.</summary>
    internal static (Embed[] Embeds, MessageComponent Components) BuildEmbeds(Poll poll, string language, DateTimeOffset now, string? dashboardUrl)
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
            lines.Append(CultureInfo.InvariantCulture, $"`{option.Position + 1}` {Icon(option.Kind)} **{Format.Sanitize(option.Label)}**\n")
                .Append(CultureInfo.InvariantCulture, $"{new string('▰', filled)}{new string('▱', 8 - filled)} {T("Polls_VoteCount", count)}\n");
        }
        var leader = poll.Leader;
        string footer = closed
            ? leader is null ? T("Polls_NoVotes") : T("Polls_WinnerLine", Format.Sanitize(leader.Label), poll.Count(leader.Position), voters)
            : T("Polls_EndsAt", $"<t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>");
        var embed = new EmbedBuilder().WithTitle(Trim((closed ? "🏁 " : "🗳️ ") + poll.Question, 256)).WithDescription(Trim(lines + "\n" + footer, 4096))
            .WithColor(closed ? new Color(0xF1C40F) : new Color(0x5865F2)).WithFooter(T("Polls_Voters", voters)).Build();
        var components = new ComponentBuilder();
        if (!closed)
        {
            var menu = new SelectMenuBuilder().WithCustomId($"{PickPrefix}:{poll.GuildId}:{poll.Id:N}").WithMinValues(1)
                .WithMaxValues(poll.Multiple ? poll.Options.Count : 1).WithPlaceholder(T(poll.Multiple ? "Polls_PickSeveral" : "Polls_PickOne"));
            foreach (var option in poll.Options)
                menu.AddOption(Trim($"{option.Position + 1}. {option.Label}", 100), option.Position.ToString(CultureInfo.InvariantCulture));
            components.WithSelectMenu(menu, 0).WithButton(T("Polls_ClearVote"), $"{ClearPrefix}:{poll.GuildId}:{poll.Id:N}", ButtonStyle.Secondary, row: 1);
        }
        if (dashboardUrl is not null)
            components.WithButton(T("Polls_OpenDashboard"), style: ButtonStyle.Link, url: dashboardUrl, row: 1);
        return ([embed], components.Build());
    }

    /// <summary>
    /// The preset line for an option: what matters for its kind, e.g. "2016 · 3 seasons, 28 episodes · Drama, Sci-Fi ·
    /// ★ 8.2 · Ended" for a series or "2024 · 166 min · Science Fiction · ★ 8.2" for a film.
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

    /// <summary>The colour of the most common kind among the options.</summary>
    private static Color AccentFor(Poll poll) => poll.Options.GroupBy(option => option.Kind).OrderByDescending(group => group.Count()).First().Key switch
    {
        "game" => new Color(0x57F287), "series" => new Color(0x3498DB), "film" => new Color(0xE74C3C), "anime" => new Color(0xEB459E), _ => new Color(0x5865F2)
    };

    private string? DashboardUrl(Poll poll) =>
        configuration.GetValue<bool>("Dashboard:Enabled") && Uri.TryCreate(configuration["Dashboard:PublicUrl"], UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
            ? new Uri(origin, poll.GroupId is { } group ? $"/groups/{poll.GuildId}/{group}#votes" : $"/votes/{poll.GuildId}#votes").ToString()
            : null;

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vote {PollId} couldn't be posted or updated in Discord ({Status}); it stays on the dashboard")]
    private static partial void LogPostFailed(ILogger logger, Guid pollId, System.Net.HttpStatusCode? status);
}
