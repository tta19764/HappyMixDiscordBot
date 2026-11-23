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
/// Votes in Discord. Every vote is a Discord poll, so people vote with Discord's own poll controls and Discord shows the
/// result; the bot copies the votes so the dashboard stays in step.
/// <list type="bullet">
/// <item>A vote on games, series, films or anime gets an info card right above the poll: each option's poster, linked
/// title, details and watch progress.</item>
/// <item>A vote for chosen members goes into a private thread with just them, so nobody else sees it or can vote.</item>
/// </list>
/// Messages from earlier versions that use the bot's own vote menu keep working.
/// </summary>
public sealed partial class PollMessages(DiscordSocketClient discord, IPollStore polls, IMediaCatalog catalog, IWatchStore watchlist,
    ISender sender, EventNotificationSettings notifications, IOptions<CommunityDefaults> community, IConfiguration configuration,
    IClock clock, ILogger<PollMessages> logger)
{
    public const string PickPrefix = "poll:pick";
    public const string ClearPrefix = "poll:clear";
    /// <summary>The answer buttons of a buy vote: <c>poll:buy:&lt;guild&gt;:&lt;poll&gt;:&lt;position&gt;</c>.</summary>
    public const string BuyPrefix = "poll:buy";
    /// <summary>Discord's limits for polls: answer text length and longest duration in hours.</summary>
    public const int NativeAnswerLength = 55;
    public const int NativeMaxHours = 768;
    /// <summary>Discord allows at most 40 components in one message.</summary>
    internal const int ComponentLimit = 40;

    /// <summary>Whether a vote is for chosen members only, and so goes into a private thread.</summary>
    public static bool IsPrivate(Poll poll) => !poll.Everyone && poll.GroupId is null;

    /// <summary>Posts the vote if it has a channel and no message yet, or brings the message up to date.</summary>
    public async Task SyncAsync(Guid pollId, ulong guildId, CancellationToken cancellationToken = default)
    {
        if (await polls.GetAsync(guildId, pollId) is { ChannelId: not null } poll)
            await SyncAsync(poll, cancellationToken);
    }

    public async Task SyncAsync(Poll poll, CancellationToken cancellationToken = default)
    {
        if (poll.ChannelId is not { } channelId || await ChannelAsync(poll.GuildId, channelId, cancellationToken) is not { } channel)
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
            LogPostFailed(logger, poll.Id, poll.GuildId, error.HttpCode);
            await polls.SetMessageAsync(poll.Id, channelId, poll.MessageId, true, poll.Native);
        }
    }

    /// <summary>
    /// After a Steam import: marks the person "Already have" in open buy votes for games they now own and updates those
    /// votes' Discord messages. Discord trouble is logged by the sync and never fails the import.
    /// </summary>
    public async Task SyncBuyVotesAsync(ulong userId, CancellationToken cancellationToken = default)
    {
        if (await sender.Send(new SyncBuyVotesCommand(userId), cancellationToken) is not { IsSuccess: true } changed)
            return;
        foreach (var (guildId, pollId) in changed.Value)
        {
            try
            {
                await SyncAsync(pollId, guildId, cancellationToken);
            }
            catch (Exception error) when (error is Discord.Net.HttpException or TimeoutException or InvalidOperationException)
            {
                // The vote is saved; its message catches up on the next vote or when voting ends.
            }
        }
    }

    /// <summary>A vote added or removed with Discord's poll controls: copied into the vote right away.</summary>
    public Task OnNativeVoteAsync(ulong guildId, ulong userId, ulong messageId, ulong answerId, bool added) =>
        sender.Send(new CopyDiscordPollVoteCommand(guildId, userId, messageId, (int)answerId - 1, added));

    /// <summary>Copies the current votes of an open Discord poll, catching up on anything missed while offline.</summary>
    public async Task ReconcileAsync(Poll poll, CancellationToken cancellationToken = default)
    {
        if (!poll.Native || poll.MessageId is not { } messageId || poll.ChannelId is not { } channelId ||
            await ChannelAsync(poll.GuildId, channelId, cancellationToken) is not { } channel ||
            await channel.GetMessageAsync(messageId, options: new RequestOptions { CancelToken = cancellationToken }) is not IUserMessage message)
            return;
        await CopyVotesAsync(poll, message, cancellationToken);
    }

    /// <summary>The channel a group's votes go to: its notification channel, when notifications are on.</summary>
    public async Task<ulong?> GroupChannelAsync(ulong guildId, Guid groupId) =>
        await notifications.GetEffectiveAsync(guildId, "group", groupId) is { Route: { Enabled: true } route } && route.Channel() is > 0 and var channel
            ? channel : null;

    /// <summary>
    /// Posts the vote: a private thread first when it's for chosen members, then the info card when options have
    /// catalog details, then the Discord poll.
    /// </summary>
    private async Task PostAsync(Poll poll, ITextChannel channel, DateTimeOffset now, RequestOptions options)
    {
        ITextChannel target = channel;
        if (IsPrivate(poll) && channel is not IThreadChannel)
        {
            try
            {
                var thread = await channel.CreateThreadAsync(Trim("🗳️ " + poll.Question, 100), ThreadType.PrivateThread,
                    ThreadArchiveDuration.OneWeek, invitable: false, options: options);
                foreach (ulong member in poll.Audience)
                    if (await channel.Guild.GetUserAsync(member, CacheMode.AllowDownload, options) is { } user)
                        await thread.AddUserAsync(user, options);
                target = thread;
            }
            catch (Discord.Net.HttpException)
            {
                LogThreadFailed(logger, poll.Id, poll.GuildId, channel.Name);
                throw;
            }
        }

        if (poll.IsBuy)
        {
            // A buy vote is the bot's own card with four buttons, so votes counted from Steam libraries show too.
            var card = await target.SendMessageAsync(components: BuildBuyCard(poll, await LanguageAsync(poll), now, DashboardUrl(poll)),
                flags: MessageFlags.ComponentsV2, allowedMentions: AllowedMentions.None, options: options);
            await polls.SetMessageAsync(poll.Id, target.Id, card.Id, false);
            LogPosted(logger, poll.Id, poll.GuildId, target.Name, "buy vote card");
            return;
        }
        if (poll.Options.Any(option => option.Media is not null))
        {
            string language = await LanguageAsync(poll);
            var (media, progress) = await DetailsAsync(poll, options.CancelToken);
            await target.SendMessageAsync(components: BuildCards(poll, media, progress, language, now, DashboardUrl(poll), interactive: false),
                flags: MessageFlags.ComponentsV2, allowedMentions: AllowedMentions.None, options: options);
        }
        var message = await target.SendMessageAsync(poll: NativePoll(poll, now), allowedMentions: AllowedMentions.None, options: options);
        await polls.SetMessageAsync(poll.Id, target.Id, message.Id, false, native: true);
        LogPosted(logger, poll.Id, poll.GuildId, target.Name, target is IThreadChannel ? "Discord poll in a private thread" : "Discord poll");
    }

    /// <summary>The vote's channel or thread, from the cache or from Discord (threads that went quiet aren't cached).</summary>
    private async Task<ITextChannel?> ChannelAsync(ulong guildId, ulong channelId, CancellationToken cancellationToken)
    {
        if (discord.GetGuild(guildId)?.GetChannel(channelId) is ITextChannel cached)
            return cached;
        try
        {
            return await discord.Rest.GetChannelAsync(channelId, new RequestOptions { CancelToken = cancellationToken }) is ITextChannel fetched && fetched.GuildId == guildId
                ? fetched : null;
        }
        catch (Discord.Net.HttpException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Vote {PollId} in server {GuildId} is for chosen members, but a private thread couldn't be created in #{Channel}; give the bot Create Private Threads and Send Messages in Threads there. It stays on the dashboard")]
    private static partial void LogThreadFailed(ILogger logger, Guid pollId, ulong guildId, string channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vote {PollId} in server {GuildId} posted in #{Channel} as {Format}")]
    private static partial void LogPosted(ILogger logger, Guid pollId, ulong guildId, string channel, string format);

    private async Task SyncNativeAsync(Poll poll, ITextChannel channel, ulong messageId, bool closed, RequestOptions options)
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

    private async Task UpdateCardsAsync(Poll poll, ITextChannel channel, ulong messageId, bool closed, RequestOptions options,
        CancellationToken cancellationToken)
    {
        string language = await LanguageAsync(poll);
        var (media, progress) = await DetailsAsync(poll, cancellationToken);
        var cards = poll.IsBuy ? BuildBuyCard(poll, language, clock.UtcNow, DashboardUrl(poll))
            : BuildCards(poll, media, progress, language, clock.UtcNow, DashboardUrl(poll));
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
        Question = new PollMediaProperties { Text = Trim(poll.Question, 300) },
        Answers = poll.Options.OrderBy(option => option.Position)
            .Select(option => new PollMediaProperties { Text = Trim(option.Label, NativeAnswerLength), Emoji = option.Kind == "custom" ? null : new Emoji(Icon(option.Kind)) })
            .ToList(),
        Duration = (uint)Math.Clamp(Math.Ceiling((poll.ClosesAt - now).TotalHours), 1, NativeMaxHours),
        AllowMultiselect = poll.Multiple,
        LayoutType = PollLayout.Default
    };

    /// <summary>
    /// The card message (Discord's component layout): a header, one section per option with its poster, linked title,
    /// details, watch progress and vote bar, and the vote menu. Once voting ends, the winner leads with a large poster.
    /// </summary>
    /// <param name="interactive">
    /// False for the info card above a Discord poll: no vote bars, counts or menu, because people vote in the poll.
    /// </param>
    internal static MessageComponent BuildCards(Poll poll, IReadOnlyDictionary<MediaRef, MediaInfo> media,
        IReadOnlyDictionary<MediaRef, WatchItem> progress, string language, DateTimeOffset now, string? dashboardUrl, bool interactive = true)
    {
        string T(string key, params object[] values) => NotificationText.Get(language, key, values);
        bool closed = interactive && poll.IsClosed(now);
        int voters = poll.Votes.Select(vote => vote.UserId).Distinct().Count();
        int max = Math.Max(1, poll.Options.Max(option => poll.Count(option.Position)));
        var leader = interactive ? poll.Leader : null;
        MediaInfo? Info(PollOption option) => option.Media is { } reference && media.TryGetValue(reference, out var info) ? info : null;

        // Fixed parts: container, header, two separators and footer, plus the menu row and button row below it.
        int below = closed ? (dashboardUrl is null ? 0 : 2) : 2 + 1 + 1 + (dashboardUrl is null ? 0 : 1);
        int used = 5 + below + poll.Options.Count; // every option needs at least one text component
        var container = new ContainerBuilder().WithAccentColor(closed ? new Color(0xF1C40F) : AccentFor(poll));
        string mentions = interactive && poll.Audience.Count is > 0 and <= 25 && !closed ? "\n" + string.Join(" ", poll.Audience.Select(id => $"<@{id}>")) : "";
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
            if (interactive)
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
        string footer = !interactive
            ? T("Polls_VoteBelow") + " · " + T("Polls_EndsAt", $"<t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>")
            : closed
            ? leader is null ? T("Polls_NoVotes") : T("Polls_WinnerLine", Format.Sanitize(leader.Label), poll.Count(leader.Position), voters)
            : T("Polls_EndsAt", $"<t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>") + " · " + T(poll.Multiple ? "Polls_PickSeveral" : "Polls_PickOne")
              + " · " + T("Polls_Voters", voters);
        container.AddComponent(new TextDisplayBuilder("-# " + footer));

        var message = new ComponentBuilderV2().AddComponent(container);
        if (!interactive)
        {
            if (dashboardUrl is not null)
                message.AddComponent(new ActionRowBuilder().WithButton(new ButtonBuilder().WithLabel(T("Polls_OpenDashboard")).WithStyle(ButtonStyle.Link).WithUrl(dashboardUrl)));
        }
        else if (!closed)
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

    /// <summary>
    /// A buy vote's card: the game (linked to its store page), each answer with its count and who picked it, and one
    /// button per answer while voting is open. Votes counted from Steam libraries are ordinary "Already have" votes.
    /// </summary>
    internal static MessageComponent BuildBuyCard(Poll poll, string language, DateTimeOffset now, string? dashboardUrl)
    {
        string T(string key, params object[] values) => NotificationText.Get(language, key, values);
        bool closed = poll.IsClosed(now);
        int voters = poll.Votes.Select(vote => vote.UserId).Distinct().Count();
        int max = Math.Max(1, poll.Options.Max(option => poll.Count(option.Position)));
        string game = BuyVote.StoreUrl(poll.GameKey) is { } store ? $"[{Format.Sanitize(poll.GameName ?? poll.Question)}]({store})" : Format.Sanitize(poll.GameName ?? poll.Question);
        var container = new ContainerBuilder().WithAccentColor(closed ? new Color(0xF1C40F) : new Color(0x4CAF7A));
        container.AddComponent(new TextDisplayBuilder(Trim($"## {(closed ? "🏁" : "🛒")} {Format.Sanitize(poll.Question)}\n{T("Buy_CardLead", game)}", 1000)));
        container.AddComponent(new SeparatorBuilder(true, SeparatorSpacingSize.Small, null));
        foreach (var option in poll.Options)
        {
            int count = poll.Count(option.Position);
            int filled = (int)Math.Round(count * 10.0 / max);
            var who = poll.Votes.Where(vote => vote.Position == option.Position).Select(vote => $"<@{vote.UserId}>").ToArray();
            string line = $"**{BuyVote.Emoji[option.Position % BuyVote.Emoji.Length]} {T($"Buy_Option_{option.Position}")}**"
                + $"\n`{new string('█', filled)}{new string('░', 10 - filled)}` **{count}**"
                + (who.Length == 0 ? "" : " · " + string.Join(" ", who.Take(20)) + (who.Length > 20 ? " …" : ""));
            container.AddComponent(new TextDisplayBuilder(line));
        }
        container.AddComponent(new SeparatorBuilder(true, SeparatorSpacingSize.Small, null));
        string footer = closed
            ? T("Buy_Summary", poll.Count(BuyVote.Ready), poll.Count(BuyVote.Willing), poll.Count(BuyVote.Have), poll.Count(BuyVote.Not))
            : T("Polls_EndsAt", $"<t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>") + " · " + T("Polls_Voters", voters) + " · " + T("Buy_SteamNote");
        container.AddComponent(new TextDisplayBuilder("-# " + footer));

        var message = new ComponentBuilderV2().AddComponent(container);
        if (!closed)
        {
            var answers = new ActionRowBuilder();
            foreach (var option in poll.Options)
                answers.WithButton(new ButtonBuilder(Trim(T($"Buy_Option_{option.Position}"), 80),
                    $"{BuyPrefix}:{poll.GuildId}:{poll.Id:N}:{option.Position.ToString(CultureInfo.InvariantCulture)}",
                    option.Position == BuyVote.Not ? ButtonStyle.Secondary : ButtonStyle.Primary, emote: new Emoji(BuyVote.Emoji[option.Position % BuyVote.Emoji.Length])));
            message.AddComponent(answers);
            var more = new ActionRowBuilder().WithButton(new ButtonBuilder(T("Polls_ClearVote"), $"{ClearPrefix}:{poll.GuildId}:{poll.Id:N}", ButtonStyle.Secondary));
            if (dashboardUrl is not null)
                more.WithButton(new ButtonBuilder().WithLabel(T("Polls_OpenDashboard")).WithStyle(ButtonStyle.Link).WithUrl(dashboardUrl));
            message.AddComponent(more);
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
            case "game" when info.Platforms is { Count: > 0 } platforms:
                parts.Add(string.Join(", ", platforms.Take(4)));
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Vote {PollId} in server {GuildId} couldn't be posted or updated in Discord ({Status}); check the bot's access to the channel. It stays on the dashboard")]
    private static partial void LogPostFailed(ILogger logger, Guid pollId, ulong guildId, System.Net.HttpStatusCode? status);
}
