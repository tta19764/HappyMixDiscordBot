using System.Text.RegularExpressions;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
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
public sealed record PollSuggestion(string Label, string Kind, string? Url, string Source, int TimesOffered, int TimesWon, DateTimeOffset? LastWon);

/// <summary>Everything the vote section needs: who may do what, the polls, and suggestions for a new one.</summary>
public sealed record PollBoard(ulong ViewerId, Guid? GroupId, bool CanManageTags, IReadOnlyList<string> Tags, string DefaultQuestion,
    IReadOnlyList<Poll> Open, IReadOnlyList<Poll> Ended, IReadOnlyList<PollSuggestion> Suggestions, IReadOnlyCollection<ulong> Voters);

/// <summary>Polls of a group (members only), or the server's own polls when <paramref name="GroupId"/> is null.</summary>
public sealed record GetPollBoardQuery(GroupActor Actor, Guid? GroupId) : IQuery<PollBoard>;

public sealed record SetGroupTagsCommand(GroupActor Actor, Guid GroupId, IReadOnlyList<string> Tags) : ICommand<IReadOnlyList<string>>;

/// <summary>
/// Starts a vote. Options are typed text and/or links (see <see cref="PollLinks"/>). A server poll needs
/// <paramref name="Audience"/> or <paramref name="Everyone"/>; a group poll goes to the group's members.
/// </summary>
public sealed record CreatePollCommand(GroupActor Actor, Guid? GroupId, string Question, IReadOnlyList<string> Options, bool Multiple,
    TimeSpan Duration, IReadOnlyList<ulong> Audience, bool Everyone, Guid RequestId) : ICommand<Guid>;

/// <summary>Replaces the voter's choices; an empty list takes the vote back.</summary>
public sealed record VotePollCommand(GroupActor Actor, Guid PollId, IReadOnlyList<int> Positions) : ICommand<Poll>;

/// <summary>The creator, or whoever manages the group, ends voting early.</summary>
public sealed record ClosePollCommand(GroupActor Actor, Guid PollId) : ICommand<Poll>;

internal sealed class PollHandlers(IPollStore polls, IAccountLinkStore accountLinks, IClock clock, ISender sender)
    : IQueryHandler<GetPollBoardQuery, PollBoard>, ICommandHandler<SetGroupTagsCommand, IReadOnlyList<string>>,
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
        var suggestions = Suggest(group, tags, all);
        return new PollBoard(viewer, query.GroupId, group?.Summary.CanManage == true, tags, DefaultQuestion(tags),
            visible.Where(poll => !poll.IsClosed(now)).ToArray(), visible.Where(poll => poll.IsClosed(now)).ToArray(), suggestions,
            group is null ? [] : Voters(group));
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
        foreach (string text in command.Options.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var (option, problem) = PollLinks.Parse(text, fallbackKind);
            if (option is null)
                return Fail(problem!);
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
            command.GroupId is null && command.Everyone, now, now + command.Duration, null, options, audience, []));
        return command.RequestId;
    }

    public async Task<Result<Poll>> Handle(VotePollCommand command, CancellationToken cancellationToken)
    {
        var (poll, voter, _) = await AccessAsync(command.Actor, command.PollId);
        if (poll is null)
            return Result.Failure<Poll>(NotFound);
        if (poll.IsClosed(clock.UtcNow))
            return Result.Failure<Poll>(new ErrorDescriptor("Poll.Closed", "Voting has ended."));
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
        IReadOnlyList<Poll> history)
    {
        var kinds = tags.Where(GroupTags.IsBuiltIn).Select(GroupTags.Kind).ToHashSet();
        var items = new Dictionary<string, (string Label, string Kind, string? Url, string Source, int Offered, int Won, DateTimeOffset? LastWon)>(
            StringComparer.OrdinalIgnoreCase);
        if (group is not null && kinds.Contains("game"))
            foreach (var game in group.Games)
                items[game.Name] = (game.Name, "game", game.SteamAppId is { } app ? $"https://store.steampowered.com/app/{app}" : null, "group", 0, 0, null);
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
            .OrderBy(item => item.LastWon ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.Offered)
            .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(24)
            .Select(item => new PollSuggestion(item.Label, item.Kind, item.Url, item.Source, item.Offered, item.Won, item.LastWon))
            .ToArray();
    }

    private static Result<Guid> Fail(string key) => Result.Failure<Guid>(new ErrorDescriptor("Poll.Invalid", key));
}
