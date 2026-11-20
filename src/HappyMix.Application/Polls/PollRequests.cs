using System.Text.RegularExpressions;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.Watch;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Polls;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Polls;

/// <summary>
/// Group tags. Built-in tags describe what a group does and decide which vote suggestions it gets; any other tag is a
/// plain label. A group can have several tags, so a group that plays and watches gets both kinds of suggestions.
/// </summary>
public static class GroupTags
{
    public const int MaxTags = 8;
    public const int MaxLength = 24;
    /// <summary>Built-in tags, in display order. Each is also the kind of option it suggests.</summary>
    public static readonly string[] BuiltIn = ["games", "series", "films", "anime"];

    public static bool IsBuiltIn(string tag) => BuiltIn.Contains(tag);

    /// <summary>Trimmed, lower-case, unique tags; null when one is too long or there are too many.</summary>
    public static string[]? Normalize(IEnumerable<string?> tags)
    {
        var cleaned = tags.Select(tag => Regex.Replace((tag ?? "").Trim().TrimStart('#').ToLowerInvariant(), @"\s+", " "))
            .Where(tag => tag.Length > 0).Distinct().ToArray();
        return cleaned.Length > MaxTags || cleaned.Any(tag => tag.Length > MaxLength || tag.Any(char.IsControl)) ? null : cleaned;
    }

    /// <summary>The option kind a tag suggests: the tag itself for built-ins ("films" suggests film options).</summary>
    public static string Kind(string tag) => tag switch { "games" => "game", "films" => "film", _ => tag };
}

/// <summary>An option as typed: a title, a link, or both. Links fill in the title and kind when they can.</summary>
public static partial class PollLinks
{
    /// <summary>
    /// Reads "title", "link" or "title link". Steam store and MyAnimeList links carry the title in the address; an IMDb
    /// or other link needs a title. Returns null with a reason when the option can't be used.
    /// </summary>
    public static (PollOption? Option, string? Problem) Parse(string? text, string fallbackKind)
    {
        string input = (text ?? "").Trim();
        var match = UrlPattern().Match(input);
        string title = (match.Success ? input.Remove(match.Index, match.Length) : input).Trim(' ', '-', '–', '|', ':');
        string? url = null;
        string kind = fallbackKind;
        if (match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            url = uri.GetLeftPart(UriPartial.Path);
            string host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (host)
            {
                case "store.steampowered.com" when segments is ["app", _, ..]:
                    kind = "game";
                    if (title.Length == 0 && segments.Length > 2) title = Words(segments[2]);
                    break;
                case "myanimelist.net" when segments is ["anime", _, ..]:
                case "anilist.co" when segments is ["anime", _, ..]:
                    kind = "anime";
                    if (title.Length == 0 && segments.Length > 2) title = Words(segments[2]);
                    break;
                case "store.epicgames.com" when segments.Length >= 2 && segments[^2] == "p":
                case "gog.com" when segments.Length >= 2 && segments[^2] == "game":
                case "igdb.com" when segments is ["games", _, ..]:
                    kind = "game";
                    if (title.Length == 0) title = CultureInfoTitle(Words(segments[host == "igdb.com" ? 1 : ^1]));
                    break;
                case "imdb.com" or "m.imdb.com" when segments is ["title", _, ..]:
                    if (fallbackKind is not ("series" or "film" or "anime")) kind = "series";
                    break;
            }
        }
        else if (match.Success)
            return (null, "Polls_BadLink");
        if (title.Length == 0)
            return (null, url is null ? "Polls_EmptyOption" : "Polls_LinkNeedsTitle");
        if (title.Length > 80)
            title = title[..80];
        return (new PollOption(0, title, kind, url), null);
    }

    /// <summary>The first web address in the text, if any.</summary>
    public static string? FindUrl(string? text) => UrlPattern().Match(text ?? "") is { Success: true } match ? match.Value : null;

    /// <summary>"league of legends" → "League Of Legends": a readable title from a store address.</summary>
    private static string CultureInfoTitle(string words) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);

    private static string Words(string slug) =>
        Regex.Replace(Uri.UnescapeDataString(slug).Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase, 100)]
    private static partial Regex UrlPattern();
}

public static class PollRules
{
    public const int MinOptions = 2;
    public const int MaxOptions = 10;
    public const int QuestionLength = 120;
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(14);
    public const int MaxAudience = 100;
    /// <summary>Ended polls shown as history and used to remember earlier choices.</summary>
    public const int HistorySize = 30;
}

/// <summary>A ready-made option for the "new vote" form, with what the group remembers about it.</summary>
/// <param name="Wanted">Group members who marked this game "Want to play" on My games.</param>
public sealed record PollSuggestion(string Label, string Kind, string? Url, string Source, int TimesOffered, int TimesWon, DateTimeOffset? LastWon,
    int Wanted = 0);

/// <summary>Everything the vote section needs: who may do what, the polls, and suggestions for a new one.</summary>
public sealed record PollBoard(ulong ViewerId, Guid? GroupId, bool CanManageTags, IReadOnlyList<string> Tags, string DefaultQuestion,
    IReadOnlyList<Poll> Open, IReadOnlyList<Poll> Ended, IReadOnlyList<PollSuggestion> Suggestions, IReadOnlyCollection<ulong> Voters)
{
    /// <summary>Catalog details of the options of open votes, for posters, years and episodes.</summary>
    public IReadOnlyDictionary<MediaRef, MediaInfo> Media { get; init; } = new Dictionary<MediaRef, MediaInfo>();

    /// <summary>Watchlist progress of the group's titles, by catalog entry, so vote options can show "next: S2E5".</summary>
    public IReadOnlyDictionary<MediaRef, WatchItem> Progress { get; init; } = new Dictionary<MediaRef, WatchItem>();
}

/// <summary>Polls of a group (members only), or the server's own polls when <paramref name="GroupId"/> is null.</summary>
public sealed record GetPollBoardQuery(GroupActor Actor, Guid? GroupId) : IQuery<PollBoard>;

/// <summary>The tags of every group in a server, for group lists and headers.</summary>
public sealed record GetGroupTagsQuery(ulong GuildId) : IQuery<IReadOnlyDictionary<Guid, string[]>>;

public sealed record SetGroupTagsCommand(GroupActor Actor, Guid GroupId, IReadOnlyList<string> Tags) : ICommand<IReadOnlyList<string>>;

/// <summary>
/// Starts a vote. Options are typed text and/or links (see <see cref="PollLinks"/>). A server poll needs
/// <paramref name="Audience"/> or <paramref name="Everyone"/>; a group poll goes to the group's members.
/// </summary>
/// <param name="ChannelId">Discord channel to post the vote in, or null to keep it on the dashboard.</param>
public sealed record CreatePollCommand(GroupActor Actor, Guid? GroupId, string Question, IReadOnlyList<string> Options, bool Multiple,
    TimeSpan Duration, IReadOnlyList<ulong> Audience, bool Everyone, Guid RequestId, ulong? ChannelId = null) : ICommand<Guid>;

/// <summary>A vote by id, for whoever may vote in it.</summary>
public sealed record GetPollQuery(GroupActor Actor, Guid PollId) : IQuery<Poll>;

/// <summary>One vote added or removed with Discord's poll controls on the message <paramref name="MessageId"/>.</summary>
public sealed record CopyDiscordPollVoteCommand(ulong GuildId, ulong UserId, ulong MessageId, int Position, bool Added) : ICommand<bool>;

/// <summary>
/// Replaces the votes of a Discord poll with what Discord lists: account id → chosen positions. Linked accounts count
/// as one member.
/// </summary>
public sealed record SyncDiscordPollVotesCommand(ulong GuildId, Guid PollId, IReadOnlyDictionary<ulong, int[]> Votes) : ICommand<bool>;

/// <summary>Replaces the voter's choices; an empty list takes the vote back.</summary>
public sealed record VotePollCommand(GroupActor Actor, Guid PollId, IReadOnlyList<int> Positions) : ICommand<Poll>;

/// <summary>The creator, or whoever manages the group, ends voting early.</summary>
public sealed record ClosePollCommand(GroupActor Actor, Guid PollId) : ICommand<Poll>;

internal sealed class PollHandlers(IPollStore polls, IAccountLinkStore accountLinks, IClock clock, ISender sender, IMediaCatalog catalog,
    IWatchStore watchlist)
    : IQueryHandler<GetPollBoardQuery, PollBoard>, IQueryHandler<GetPollQuery, Poll>, IQueryHandler<GetGroupTagsQuery, IReadOnlyDictionary<Guid, string[]>>, ICommandHandler<SetGroupTagsCommand, IReadOnlyList<string>>,
      ICommandHandler<CopyDiscordPollVoteCommand, bool>, ICommandHandler<SyncDiscordPollVotesCommand, bool>,
      ICommandHandler<CreatePollCommand, Guid>, ICommandHandler<VotePollCommand, Poll>, ICommandHandler<ClosePollCommand, Poll>
{
    private static readonly ErrorDescriptor NotFound = new("Poll.NotFound", "That vote doesn't exist or isn't for you.");

    public async Task<Result<PollBoard>> Handle(GetPollBoardQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var links = await accountLinks.LoadAsync(actor.GuildId);
        ulong viewer = links.Member(actor.UserId);
        var now = clock.UtcNow;
        GroupDetail? group = null;
        IReadOnlyList<string> tags = [];
        if (query.GroupId is { } groupId)
        {
            if (await GroupAsync(actor, groupId) is not { } detail)
                return Result.Failure<PollBoard>(NotFound);
            group = detail;
            tags = await polls.GetTagsAsync(actor.GuildId, groupId);
        }

        var all = await polls.ListAsync(actor.GuildId, query.GroupId, PollRules.HistorySize, now);
        // Server polls are listed only to the people they were for.
        var visible = all.Where(poll => group is not null || poll.Everyone || poll.Audience.Contains(viewer) || poll.CreatorId == viewer).ToArray();
        var tracked = query.GroupId is { } id ? await watchlist.ListAsync(actor.GuildId, id) : [];
        // Members' marks: games they want rank first, and games several want join the list even if the group lacks them.
        var interest = group is not null && await sender.Send(new HappyMix.Application.Players.GetGroupGameInterestQuery(actor, group.Summary.Id), cancellationToken)
            is { IsSuccess: true } marks ? marks.Value : null;
        var suggestions = Suggest(group, tags, all, tracked, interest);
        var open = visible.Where(poll => !poll.IsClosed(now)).ToArray();
        var media = new Dictionary<MediaRef, MediaInfo>();
        foreach (var reference in open.SelectMany(poll => poll.Options).Select(option => option.Media).OfType<MediaRef>().Distinct())
            if (await catalog.GetAsync(reference, cancellationToken) is { } info)
                media[reference] = info;
        return new PollBoard(viewer, query.GroupId, group?.Summary.CanManage == true, tags, DefaultQuestion(tags),
            open, visible.Where(poll => poll.IsClosed(now)).ToArray(), suggestions, group is null ? [] : Voters(group))
        {
            Media = media,
            Progress = tracked.Where(item => item.Source is not null && item.ExternalId is not null)
                .ToDictionary(item => new MediaRef(item.Source!, item.ExternalId!))
        };
    }

    public async Task<Result<bool>> Handle(CopyDiscordPollVoteCommand command, CancellationToken cancellationToken)
    {
        if (await polls.FindByMessageAsync(command.MessageId) is not { Native: true } poll || poll.GuildId != command.GuildId ||
            poll.IsClosed(clock.UtcNow) || poll.Options.All(option => option.Position != command.Position))
            return false;
        ulong member = (await accountLinks.LoadAsync(command.GuildId)).Member(command.UserId);
        var mine = poll.Votes.Where(vote => vote.UserId == member).Select(vote => vote.Position).ToHashSet();
        if (command.Added) mine.Add(command.Position); else mine.Remove(command.Position);
        await polls.VoteAsync(poll.Id, member, mine.ToArray());
        return true;
    }

    public async Task<Result<bool>> Handle(SyncDiscordPollVotesCommand command, CancellationToken cancellationToken)
    {
        if (await polls.GetAsync(command.GuildId, command.PollId) is not { Native: true })
            return false;
        var links = await accountLinks.LoadAsync(command.GuildId);
        var members = new Dictionary<ulong, HashSet<int>>();
        foreach (var (user, positions) in command.Votes)
        {
            ulong member = links.Member(user);
            if (!members.TryGetValue(member, out var chosen))
                members[member] = chosen = [];
            chosen.UnionWith(positions);
        }
        await polls.ReplaceVotesAsync(command.PollId, members.ToDictionary(item => item.Key, item => item.Value.ToArray()));
        return true;
    }

    public async Task<Result<IReadOnlyDictionary<Guid, string[]>>> Handle(GetGroupTagsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await polls.ListTagsAsync(query.GuildId));

    public async Task<Result<Poll>> Handle(GetPollQuery query, CancellationToken cancellationToken)
    {
        var (poll, _, _) = await AccessAsync(query.Actor, query.PollId);
        return poll is null ? Result.Failure<Poll>(NotFound) : poll;
    }

    public async Task<Result<IReadOnlyList<string>>> Handle(SetGroupTagsCommand command, CancellationToken cancellationToken)
    {
        if (await GroupAsync(command.Actor, command.GroupId) is not { Summary.CanManage: true })
            return Result.Failure<IReadOnlyList<string>>(new ErrorDescriptor("Poll.Forbidden", "Only the organizer or a moderator can change the tags."));
        if (GroupTags.Normalize(command.Tags) is not { } tags)
            return Result.Failure<IReadOnlyList<string>>(new ErrorDescriptor("Poll.Tags", $"Use up to {GroupTags.MaxTags} tags of up to {GroupTags.MaxLength} characters."));
        await polls.SetTagsAsync(command.Actor.GuildId, command.GroupId, tags);
        return tags;
    }

    public async Task<Result<Guid>> Handle(CreatePollCommand command, CancellationToken cancellationToken)
    {
        var actor = command.Actor;
        var links = await accountLinks.LoadAsync(actor.GuildId);
        ulong creator = links.Member(actor.UserId);
        string fallbackKind = "custom";
        if (command.GroupId is { } groupId)
        {
            if (await GroupAsync(actor, groupId) is not { CanViewRoster: true })
                return Result.Failure<Guid>(NotFound);
            var tags = await polls.GetTagsAsync(actor.GuildId, groupId);
            fallbackKind = tags.Where(GroupTags.IsBuiltIn).Select(GroupTags.Kind).FirstOrDefault() ?? "custom";
        }
        if (await polls.GetAsync(actor.GuildId, command.RequestId) is not null)
            return command.RequestId;

        string question = (command.Question ?? "").Trim();
        if (question.Length is 0 or > PollRules.QuestionLength)
            return Fail("Polls_QuestionError");
        var options = new List<PollOption>();
        foreach (string text in command.Options.Where(item => !string.IsNullOrWhiteSpace(item)).Take(PollRules.MaxOptions + 1))
        {
            var (option, problem) = PollLinks.Parse(text, fallbackKind);
            // Catalog details (poster, year, episodes) for links; they also supply a missing title, e.g. for a bare IMDb link.
            var media = PollLinks.FindUrl(text) is { } url ? await LookupAsync(url, cancellationToken) : null;
            if (option is null && media is not null && problem == "Polls_LinkNeedsTitle")
                option = new PollOption(0, media.Title, media.Kind, PollLinks.FindUrl(text));
            if (option is null)
                return Fail(problem!);
            if (media is not null)
                option = option with { Kind = media.Kind, Media = media.Ref };
            if (!options.Any(item => string.Equals(item.Label, option.Label, StringComparison.OrdinalIgnoreCase)))
                options.Add(option with { Position = options.Count });
        }
        if (options.Count is < PollRules.MinOptions or > PollRules.MaxOptions)
            return Fail("Polls_OptionCount");
        if (command.Duration < PollRules.MinDuration || command.Duration > PollRules.MaxDuration)
            return Fail("Polls_Duration");
        ulong[] audience = [];
        if (command.GroupId is null && !command.Everyone)
        {
            audience = command.Audience.Select(links.Member).Append(creator).Distinct().ToArray();
            if (audience.Length < 2 || audience.Length > PollRules.MaxAudience)
                return Fail("Polls_Audience");
        }

        var now = clock.UtcNow;
        await polls.CreateAsync(new Poll(command.RequestId, actor.GuildId, command.GroupId, creator, question, command.Multiple,
            command.GroupId is null && command.Everyone, now, now + command.Duration, null, options, audience, [])
        {
            ChannelId = command.ChannelId
        });
        return command.RequestId;
    }

    public async Task<Result<Poll>> Handle(VotePollCommand command, CancellationToken cancellationToken)
    {
        var (poll, voter, _) = await AccessAsync(command.Actor, command.PollId);
        if (poll is null)
            return Result.Failure<Poll>(NotFound);
        if (poll.IsClosed(clock.UtcNow))
            return Result.Failure<Poll>(new ErrorDescriptor("Poll.Closed", "Voting has ended."));
        if (poll.Native && poll.MessageId is not null)
            return Result.Failure<Poll>(new ErrorDescriptor("Poll.Discord", "This vote is a Discord poll. Vote in Discord."));
        var positions = command.Positions.Distinct().ToArray();
        if (positions.Any(position => poll.Options.All(option => option.Position != position)) || (!poll.Multiple && positions.Length > 1))
            return Result.Failure<Poll>(new ErrorDescriptor("Poll.Choice", poll.Multiple ? "Pick options from this vote." : "Pick one option."));
        await polls.VoteAsync(poll.Id, voter, positions);
        return (await polls.GetAsync(command.Actor.GuildId, poll.Id))!;
    }

    public async Task<Result<Poll>> Handle(ClosePollCommand command, CancellationToken cancellationToken)
    {
        var (poll, voter, manages) = await AccessAsync(command.Actor, command.PollId);
        if (poll is null)
            return Result.Failure<Poll>(NotFound);
        if (poll.CreatorId != voter && !manages)
            return Result.Failure<Poll>(new ErrorDescriptor("Poll.Forbidden", "Only whoever started the vote can end it early."));
        await polls.CloseAsync(poll.Id, clock.UtcNow);
        return (await polls.GetAsync(command.Actor.GuildId, poll.Id))!;
    }

    /// <summary>The poll if the actor may vote in it, their member id, and whether they manage its group.</summary>
    private async Task<(Poll? Poll, ulong Voter, bool Manages)> AccessAsync(GroupActor actor, Guid pollId)
    {
        var links = await accountLinks.LoadAsync(actor.GuildId);
        ulong voter = links.Member(actor.UserId);
        if (await polls.GetAsync(actor.GuildId, pollId) is not { } poll)
            return (null, voter, false);
        if (poll.GroupId is { } groupId)
            return await GroupAsync(actor, groupId) is { CanViewRoster: true } detail ? (poll, voter, detail.Summary.CanManage) : (null, voter, false);
        bool allowed = poll.Everyone || poll.CreatorId == voter || poll.Audience.Contains(voter);
        return allowed ? (poll, voter, actor.CanModerate) : (null, voter, false);
    }

    /// <summary>Catalog details for a link, waiting at most a few seconds so a slow catalog never blocks a vote.</summary>
    private async Task<MediaInfo?> LookupAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            return await catalog.LookupAsync(url, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<GroupDetail?> GroupAsync(GroupActor actor, Guid groupId) =>
        (await sender.Send(new GetGroupDetailQuery(actor, groupId))) is { IsSuccess: true } result ? result.Value : null;

    private static ulong[] Voters(GroupDetail group) => group.Members.Append(group.Summary.OrganizerId).Distinct().ToArray();

    private static string DefaultQuestion(IReadOnlyList<string> tags)
    {
        var builtIn = tags.Where(GroupTags.IsBuiltIn).ToArray();
        return builtIn switch
        {
            ["games"] => "Polls_Question_games",
            [var only] => "Polls_Question_" + only,
            [] => "Polls_Question_default",
            _ when builtIn.Contains("games") => "Polls_Question_mixed",
            _ => "Polls_Question_watch"
        };
    }

    /// <summary>
    /// Options worth offering: the group's games (games tag) and everything offered in earlier votes, with how often it
    /// was offered and won. Options nobody picked recently come first, so the list nudges toward something new.
    /// </summary>
    private PollSuggestion[] Suggest(GroupDetail? group, IReadOnlyList<string> tags,
        IReadOnlyList<Poll> history, IReadOnlyList<WatchItem> tracked, HappyMix.Application.Players.GroupGameInterest? interest = null)
    {
        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (group is not null && interest is not null)
            foreach (var game in group.Games)
                if (interest.Games.TryGetValue(game.Id, out var marks) && marks.Want.Count > 0)
                    wanted[game.Name] = marks.Want.Count;
        var kinds = tags.Where(GroupTags.IsBuiltIn).Select(GroupTags.Kind).ToHashSet();
        var items = new Dictionary<string, (string Label, string Kind, string? Url, string Source, int Offered, int Won, DateTimeOffset? LastWon)>(
            StringComparer.OrdinalIgnoreCase);
        if (group is not null && kinds.Contains("game"))
            foreach (var game in group.Games)
                items[game.Name] = (game.Name, "game", game.SteamAppId is { } app ? $"https://store.steampowered.com/app/{app}" : null, "group", 0, 0, null);
        if (interest is not null && kinds.Contains("game"))
            foreach (var popular in interest.Popular)
            {
                items.TryAdd(popular.Name, (popular.Name, "game", popular.Provider == "steam" ? $"https://store.steampowered.com/app/{popular.ExternalId}" : null,
                    "wanted", 0, 0, null));
                wanted[popular.Name] = popular.Want.Count;
            }
        foreach (var item in tracked.Where(item => item.Status is WatchStatus.Planned or WatchStatus.Watching))
            items[item.Title] = (item.Title, item.Kind, item.Url, "watchlist", 0, 0, null);
        var now = clock.UtcNow;
        foreach (var poll in history)
        {
            var winner = poll.IsClosed(now) ? poll.Leader : null;
            foreach (var option in poll.Options)
            {
                // A group without built-in tags remembers everything; a tagged group only what fits its tags.
                if (kinds.Count > 0 && option.Kind != "custom" && !kinds.Contains(option.Kind))
                    continue;
                var current = items.TryGetValue(option.Label, out var found) ? found : (option.Label, option.Kind, option.Url, "history", 0, 0, null);
                bool won = winner?.Position == option.Position;
                items[option.Label] = (current.Label, current.Kind, current.Url ?? option.Url, current.Source, current.Offered + 1,
                    current.Won + (won ? 1 : 0), won && (current.LastWon is null || poll.EndedAt > current.LastWon) ? poll.EndedAt : current.LastWon);
            }
        }
        return items.Values
            .OrderByDescending(item => wanted.GetValueOrDefault(item.Label))
            .ThenBy(item => item.LastWon ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.Offered)
            .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(24)
            .Select(item => new PollSuggestion(item.Label, item.Kind, item.Url, item.Source, item.Offered, item.Won, item.LastWon,
                wanted.GetValueOrDefault(item.Label)))
            .ToArray();
    }

    private static Result<Guid> Fail(string key) => Result.Failure<Guid>(new ErrorDescriptor("Poll.Invalid", key));
}
