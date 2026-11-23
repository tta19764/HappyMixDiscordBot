using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Abstractions.Polls;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Polls;

/// <summary>
/// Starts a "will you buy this game" vote in a group. <paramref name="Game"/> is a name, a Steam store link or a My games
/// key (<c>steam:730</c>); <paramref name="QuestionFormat"/> is the question in the asker's language with {0} for the game.
/// Members who own the game in their imported Steam library are counted as "Already have" straight away.
/// </summary>
public sealed record CreateBuyVoteCommand(GroupActor Actor, Guid GroupId, string Game, string QuestionFormat, TimeSpan Duration, Guid RequestId,
    ulong? ChannelId = null) : ICommand<Guid>;

/// <summary>
/// After a Steam import: marks the person "Already have" in open buy votes of their groups for games they now own.
/// Returns the votes that changed, so their Discord messages can be updated.
/// </summary>
public sealed record SyncBuyVotesCommand(ulong UserId) : ICommand<IReadOnlyList<(ulong GuildId, Guid PollId)>>;

internal sealed class BuyVoteHandlers(IPollStore polls, IAccountLinkStore accountLinks, IPlayerGameStore players, IGuildGroupStore groups,
    IClock clock, ISender sender) :
    ICommandHandler<CreateBuyVoteCommand, Guid>, ICommandHandler<SyncBuyVotesCommand, IReadOnlyList<(ulong GuildId, Guid PollId)>>
{
    private static readonly ErrorDescriptor NotFound = new("Poll.NotFound", "That vote doesn't exist or isn't for you.");

    public async Task<Result<Guid>> Handle(CreateBuyVoteCommand command, CancellationToken cancellationToken)
    {
        var actor = command.Actor;
        if (await sender.Send(new GetGroupDetailQuery(actor, command.GroupId), cancellationToken) is not { IsSuccess: true, Value: { CanViewRoster: true } group })
            return Result.Failure<Guid>(NotFound);
        if (await polls.GetAsync(actor.GuildId, command.RequestId) is not null)
            return command.RequestId;
        if (command.Duration < PollRules.MinDuration || command.Duration > PollRules.MaxDuration)
            return Fail("Polls_Duration");

        var links = await accountLinks.LoadAsync(actor.GuildId);
        var voters = group.Members.Append(group.Summary.OrganizerId).Select(links.Member).Distinct().ToArray();
        var libraries = await players.ListForAsync(voters.SelectMany(links.Accounts).ToArray());
        if (Resolve(command.Game, group.Games, libraries) is not { } game)
            return Fail("Buy_GameMissing");

        string question = string.Format(System.Globalization.CultureInfo.InvariantCulture, command.QuestionFormat, game.Name);
        if (question.Length > PollRules.QuestionLength)
            question = question[..(PollRules.QuestionLength - 1)] + "…";
        var now = clock.UtcNow;
        var options = BuyVote.Labels.Select((label, position) => new PollOption(position, label, BuyVote.Kind, null)).ToArray();
        var poll = new Poll(command.RequestId, actor.GuildId, command.GroupId, links.Member(actor.UserId), question, false, false, now,
            now + command.Duration, null, options, [], [])
        {
            ChannelId = command.ChannelId,
            Purpose = PollPurpose.Buy,
            GameKey = game.Key,
            GameName = game.Name
        };
        await polls.CreateAsync(poll);
        foreach (ulong member in voters.Where(member => Owns(libraries, links.Accounts(member), game.Key, game.Name)))
            await polls.VoteAsync(poll.Id, member, [BuyVote.Have]);
        return command.RequestId;
    }

    public async Task<Result<IReadOnlyList<(ulong GuildId, Guid PollId)>>> Handle(SyncBuyVotesCommand command, CancellationToken cancellationToken)
    {
        var changed = new List<(ulong GuildId, Guid PollId)>();
        var open = await polls.ListOpenBuyAsync(clock.UtcNow);
        foreach (var guild in open.Where(poll => poll.GroupId is not null && poll.GameKey is not null).GroupBy(poll => poll.GuildId))
        {
            var links = await accountLinks.LoadAsync(guild.Key);
            ulong member = links.Member(command.UserId);
            var library = await players.ListForAsync(links.Accounts(member));
            var groupList = await groups.ListAsync(guild.Key);
            foreach (var poll in guild)
            {
                if (groupList.FirstOrDefault(item => item.Id == poll.GroupId) is not { } group
                    || !group.Members.Append(group.OrganizerId).Any(account => links.Same(account, member))
                    || poll.Votes.Any(vote => vote.UserId == member && vote.Position == BuyVote.Have)
                    || !Owns(library, links.Accounts(member), poll.GameKey!, poll.GameName))
                    continue;
                await polls.VoteAsync(poll.Id, member, [BuyVote.Have]);
                changed.Add((poll.GuildId, poll.Id));
            }
        }
        return changed;
    }

    /// <summary>
    /// The game asked about: a Steam link or key, a game of the group, or a game in a member's library, matched by name;
    /// anything else is kept as a plain name. Null when nothing was typed.
    /// </summary>
    internal static (string Key, string Name)? Resolve(string? text, IReadOnlyList<GroupGame> groupGames, IReadOnlyList<PlayerGame> libraries)
    {
        string typed = (text ?? "").Trim();
        if (typed.Length == 0)
            return null;
        string? appId = null;
        string name = typed;
        if (typed.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) && typed[6..].All(char.IsAsciiDigit) && typed.Length > 6)
            (appId, name) = (typed[6..], "");
        else if (PollLinks.FindUrl(typed) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith("store.steampowered.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["app", var id, ..] && id.All(char.IsAsciiDigit))
        {
            appId = id;
            name = PollLinks.Parse(typed, "game").Option?.Label ?? "";
        }
        else
        {
            appId = groupGames.FirstOrDefault(game => string.Equals(game.Name, typed, StringComparison.OrdinalIgnoreCase))?.SteamAppId
                ?? libraries.FirstOrDefault(game => game.Provider == "steam" && string.Equals(game.Name, typed, StringComparison.OrdinalIgnoreCase))?.ExternalId;
        }
        if (appId is null)
            return ("name:" + typed.ToLowerInvariant(), typed.Length > 80 ? typed[..80] : typed);
        // A known name beats the one guessed from the store address.
        string known = groupGames.FirstOrDefault(game => game.SteamAppId == appId)?.Name
            ?? libraries.FirstOrDefault(game => game.Key == "steam:" + appId)?.Name ?? name;
        return ("steam:" + appId, known.Length == 0 ? "Steam app " + appId : known.Length > 80 ? known[..80] : known);
    }

    /// <summary>
    /// Whether one of the accounts has the game in an imported Steam library (by key, or by name for typed games). Imported
    /// games carry playtimes, also games first added by hand and found in the library later.
    /// </summary>
    internal static bool Owns(IReadOnlyList<PlayerGame> libraries, IReadOnlyCollection<ulong> accounts, string gameKey, string? gameName) =>
        libraries.Any(game => (game.FromSteam || game.PlaytimeMinutes is not null) && accounts.Contains(game.UserId)
            && (game.Key == gameKey || gameKey.StartsWith("name:", StringComparison.Ordinal) && string.Equals(game.Name, gameName, StringComparison.OrdinalIgnoreCase)));

    private static Result<Guid> Fail(string key) => Result.Failure<Guid>(new ErrorDescriptor("Poll.Invalid", key));
}
