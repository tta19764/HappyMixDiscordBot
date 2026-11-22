using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Stats;

/// <summary>
/// Reads recent matches of people who share their stats, finds matches two or more members of a group played together,
/// and posts each new one once in the group's channel when the organizer turned that on. Returns how many were posted.
/// </summary>
/// <param name="GuildIds">The servers the bot is in, whose groups are checked.</param>
public sealed record RefreshMatchesCommand(IReadOnlyCollection<ulong> GuildIds, int Limit = 20) : ICommand<int>;

/// <summary>Reads one person's recent matches now (the Refresh buttons). Returns how many matches came back.</summary>
/// <param name="GuildId">The server the click came from, so lookup problems show in that server's Logs.</param>
public sealed record RefreshPlayerMatchesCommand(ulong UserId, ulong? GuildId = null) : ICommand<int>;

/// <summary>
/// The organizer's switches: also post match recaps in the group's notification channel, and (when
/// <paramref name="Digest"/> is given) the Monday summary of last week.
/// </summary>
public sealed record SetRecapChannelCommand(GroupActor Actor, Guid GroupId, bool Post, bool? Digest = null) : ICommand<bool>;

public static class MatchRecaps
{
    /// <summary>How often one person's match history is read.</summary>
    public static readonly TimeSpan FetchEvery = TimeSpan.FromMinutes(30);
    /// <summary>The Stats tab lists games together from this far back.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromDays(14);
    /// <summary>Only matches this recent are posted, so turning on the channel (or a new member) doesn't post old games.</summary>
    public static readonly TimeSpan PostWithin = TimeSpan.FromDays(1);
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    public const int MaxShown = 10;

    /// <summary>Matches at least two of <paramref name="members"/> played, newest first.</summary>
    public static IReadOnlyList<GroupRecap> Find(Guid groupId, IReadOnlyCollection<ulong> members, IEnumerable<PlayerMatch> matches,
        IReadOnlyCollection<StatsGame> games) =>
        matches.Where(match => members.Contains(match.UserId) && games.Contains(match.Game))
            .GroupBy(match => (match.Game, match.MatchId))
            .Select(match => match.DistinctBy(player => player.UserId).ToArray())
            .Where(players => players.Length >= 2)
            .Select(players => new GroupRecap(groupId, players[0].Game, players[0].MatchId, players.Min(player => player.StartedAt),
                players.Max(player => player.DurationSeconds), players.OrderByDescending(player => player.Kills).ToArray()))
            .OrderByDescending(recap => recap.StartedAt)
            .ToArray();
}

internal sealed partial class MatchRecapHandlers(IGameStatsStore store, IGameStatsDirectory directory, IPlayerGameStore players, IGuildGroupStore groups,
    IEnumerable<IRecapPublisher> publishers, IClock clock, ISender sender, Microsoft.Extensions.Logging.ILogger<MatchRecapHandlers> logger) :
    ICommandHandler<RefreshMatchesCommand, int>, ICommandHandler<SetRecapChannelCommand, bool>, ICommandHandler<RefreshPlayerMatchesCommand, int>
{
    public async Task<Result<int>> Handle(RefreshMatchesCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sharers = await store.ListAllSharingAsync();
        var games = directory.Cs2Enabled ? new[] { StatsGame.Dota2, StatsGame.Cs2 } : [StatsGame.Dota2];

        // Oldest history first; each person's games are read at most every 30 minutes.
        var due = new List<(ulong User, StatsGame Game, DateTimeOffset Last)>();
        foreach (ulong user in sharers)
            foreach (var game in games)
            {
                var last = await store.MatchesFetchedAtAsync(user, game);
                if (last is null || now - last >= MatchRecaps.FetchEvery)
                    due.Add((user, game, last ?? DateTimeOffset.MinValue));
            }
        foreach (var (user, game, _) in due.OrderBy(item => item.Last).Take(Math.Max(1, command.Limit)))
        {
            // Without an OpenDota key the day's budget is shared with Refresh clicks; the rest waits for tomorrow.
            if (!directory.BackgroundBudgetLeft)
                break;
            await FetchMatchesAsync(user, game, null, cancellationToken);
        }
        await store.PruneMatchesAsync(now - MatchRecaps.KeepFor);

        int posted = 0;
        foreach (ulong guildId in command.GuildIds)
        {
            var gameLists = (await groups.ListGamesAsync(guildId)).ToLookup(game => game.GroupId);
            foreach (var group in await groups.ListAsync(guildId))
            {
                var groupGames = GameStats.GamesOf(gameLists[group.Id]);
                var members = group.Members.Append(group.OrganizerId).Distinct().Where(sharers.Contains).ToArray();
                if (groupGames.Count == 0 || members.Length < 2)
                    continue;
                var recent = await store.ListMatchesAsync(members, now - MatchRecaps.PostWithin);
                bool post = await store.GetRecapChannelAsync(guildId, group.Id);
                foreach (var recap in MatchRecaps.Find(group.Id, members, recent, groupGames).Reverse())
                {
                    // Every recap is marked, posted or not, so turning the channel on later doesn't post old games.
                    if (!await store.MarkRecapAsync(guildId, group.Id, recap.Game, recap.MatchId, now) || !post)
                        continue;
                    foreach (var publisher in publishers)
                        await publisher.PublishAsync(guildId, group.Name, recap, cancellationToken);
                    posted++;
                }
            }
        }
        return posted;
    }

    public async Task<Result<int>> Handle(RefreshPlayerMatchesCommand command, CancellationToken cancellationToken)
    {
        int found = 0;
        foreach (var game in directory.Cs2Enabled ? new[] { StatsGame.Dota2, StatsGame.Cs2 } : [StatsGame.Dota2])
            found += await FetchMatchesAsync(command.UserId, game, command.GuildId, cancellationToken);
        return found;
    }

    /// <summary>Reads one person's history for one game and saves new matches; failures are logged with the reason.</summary>
    private async Task<int> FetchMatchesAsync(ulong user, StatsGame game, ulong? guildId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await store.SetMatchesFetchedAtAsync(user, game, now);
        if (await players.GetSteamAsync(user) is not { } steam)
            return 0;
        var known = (await store.ListMatchesAsync([user], now - MatchRecaps.KeepFor)).Where(match => match.Game == game)
            .Select(match => match.MatchId).ToHashSet(StringComparer.Ordinal);
        var fetched = await directory.GetMatchesAsync(user, game, steam.SteamId, known, cancellationToken);
        if (fetched.Status == StatsStatus.Error)
            using (guildId is { } guild ? logger.BeginScope(new Dictionary<string, object> { ["GuildId"] = guild }) : null)
                LogMatchesFailed(logger, game, user, fetched.Reason ?? "unknown");
        if (fetched is not { Status: StatsStatus.Ok, Profile: { Count: > 0 } matches })
            return 0;
        await store.SaveMatchesAsync(matches.Where(match => now - match.StartedAt < MatchRecaps.KeepFor).ToArray());
        return matches.Count;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "{Game} matches for {UserId} couldn't be loaded: {Reason}")]
    private static partial void LogMatchesFailed(Microsoft.Extensions.Logging.ILogger logger, StatsGame game, ulong userId, string reason);

    public async Task<Result<bool>> Handle(SetRecapChannelCommand command, CancellationToken cancellationToken)
    {
        if (await sender.Send(new GetGroupDetailQuery(command.Actor, command.GroupId), cancellationToken) is not { IsSuccess: true, Value: { CanViewRoster: true } detail })
            return Result.Failure<bool>(new ErrorDescriptor("Stats.NotFound", "That group doesn't exist or you don't have access to it."));
        if (!detail.Summary.CanManage)
            return Result.Failure<bool>(new ErrorDescriptor("Stats.Forbidden", "Only the organizer or a moderator can change this."));
        await store.SetRecapChannelAsync(command.Actor.GuildId, command.GroupId, command.Post);
        if (command.Digest is { } digest)
            await store.SetDigestChannelAsync(command.Actor.GuildId, command.GroupId, digest);
        return command.Post;
    }
}
