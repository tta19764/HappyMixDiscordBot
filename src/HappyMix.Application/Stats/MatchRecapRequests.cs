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

/// <summary>The organizer's switch: also post match recaps in the group's notification channel.</summary>
public sealed record SetRecapChannelCommand(GroupActor Actor, Guid GroupId, bool Post) : ICommand<bool>;

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

internal sealed class MatchRecapHandlers(IGameStatsStore store, IGameStatsDirectory directory, IPlayerGameStore players, IGuildGroupStore groups,
    IEnumerable<IRecapPublisher> publishers, IClock clock, ISender sender) :
    ICommandHandler<RefreshMatchesCommand, int>, ICommandHandler<SetRecapChannelCommand, bool>
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
            await store.SetMatchesFetchedAtAsync(user, game, now);
            if (await players.GetSteamAsync(user) is not { } steam)
                continue;
            var known = (await store.ListMatchesAsync([user], now - MatchRecaps.KeepFor)).Where(match => match.Game == game)
                .Select(match => match.MatchId).ToHashSet(StringComparer.Ordinal);
            var fetched = await directory.GetMatchesAsync(user, game, steam.SteamId, known, cancellationToken);
            if (fetched is { Status: StatsStatus.Ok, Profile: { Count: > 0 } matches })
                await store.SaveMatchesAsync(matches.Where(match => now - match.StartedAt < MatchRecaps.KeepFor).ToArray());
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

    public async Task<Result<bool>> Handle(SetRecapChannelCommand command, CancellationToken cancellationToken)
    {
        if (await sender.Send(new GetGroupDetailQuery(command.Actor, command.GroupId), cancellationToken) is not { IsSuccess: true, Value: { CanViewRoster: true } detail })
            return Result.Failure<bool>(new ErrorDescriptor("Stats.NotFound", "That group doesn't exist or you don't have access to it."));
        if (!detail.Summary.CanManage)
            return Result.Failure<bool>(new ErrorDescriptor("Stats.Forbidden", "Only the organizer or a moderator can change this."));
        await store.SetRecapChannelAsync(command.Actor.GuildId, command.GroupId, command.Post);
        return command.Post;
    }
}
