using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Stats;

/// <summary>One member's week: games together, wins and their kills/deaths/assists in those games.</summary>
public sealed record DigestPlayer(ulong UserId, int Games, int Wins, int Kills, int Deaths, int Assists);

/// <summary>
/// A group's week in one game: games together, the group's wins and losses (games on opposite sides count as
/// neither), the most played hero (Dota 2) or map (CS2), and the best performer.
/// </summary>
/// <param name="Best">The highest win rate among members with at least <see cref="WeeklyDigests.MinGamesForBest"/> games.</param>
public sealed record GroupDigest(StatsGame Game, int Games, int Wins, int Losses, string? MostPlayed, int MostPlayedCount, DigestPlayer? Best,
    IReadOnlyList<DigestPlayer> Players);

/// <summary>
/// Posts last week's digest on Monday mornings (10:00 in each server's zone, any run until the day ends) for groups
/// whose organizer turned it on. Each group gets one digest per week; weeks without games together are skipped.
/// </summary>
public sealed record PostWeeklyDigestsCommand(IReadOnlyCollection<ulong> GuildIds, string DefaultTimeZone) : ICommand<int>;

public static class WeeklyDigests
{
    public const int MinGamesForBest = 3;
    public const int PostFromHour = 10;

    /// <summary>One digest per game that had games together, Dota 2 first.</summary>
    public static IReadOnlyList<GroupDigest> Build(IEnumerable<GroupRecap> recaps) =>
        recaps.GroupBy(recap => recap.Game).OrderBy(game => game.Key).Select(game =>
        {
            var list = game.ToArray();
            var players = list.SelectMany(recap => recap.Players)
                .GroupBy(player => player.UserId)
                .Select(lines => new DigestPlayer(lines.Key, lines.Count(), lines.Count(line => line.Won), lines.Sum(line => line.Kills),
                    lines.Sum(line => line.Deaths), lines.Sum(line => line.Assists)))
                .OrderByDescending(player => player.Games).ThenByDescending(player => player.Wins).ThenBy(player => player.UserId)
                .ToArray();
            // Dota 2: heroes picked by members; CS2: one map per match.
            var picks = game.Key == StatsGame.Dota2
                ? list.SelectMany(recap => recap.Players).Select(player => player.Pick)
                : list.Select(recap => recap.Players[0].Pick);
            var most = picks.Where(pick => !string.IsNullOrWhiteSpace(pick)).GroupBy(pick => pick!, StringComparer.Ordinal)
                .OrderByDescending(pick => pick.Count()).ThenBy(pick => pick.Key, StringComparer.Ordinal).FirstOrDefault();
            var best = players.Where(player => player.Games >= MinGamesForBest)
                .OrderByDescending(player => (double)player.Wins / player.Games).ThenByDescending(player => player.Games)
                .ThenByDescending(player => (player.Kills + player.Assists) / (double)Math.Max(1, player.Deaths))
                .FirstOrDefault();
            return new GroupDigest(game.Key, list.Length, list.Count(recap => recap.Won == true), list.Count(recap => recap.Won == false),
                most?.Key, most?.Count() ?? 0, best, players);
        }).ToArray();

    /// <summary>Monday 00:00 of the week containing <paramref name="local"/>.</summary>
    public static DateOnly WeekStart(DateTime local) => DateOnly.FromDateTime(local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)));
}

internal sealed class WeeklyDigestHandlers(IGameStatsStore store, IGuildGroupStore groups, IGuildSettingsStore settings,
    IEnumerable<IRecapPublisher> publishers, IClock clock) : ICommandHandler<PostWeeklyDigestsCommand, int>
{
    public async Task<Result<int>> Handle(PostWeeklyDigestsCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sharers = (await store.ListAllSharingAsync()).ToHashSet();
        int posted = 0;
        foreach (ulong guildId in command.GuildIds)
        {
            var zone = LocalTimes.FindZone((await settings.GetAsync(guildId, cancellationToken))?.TimeZoneId ?? command.DefaultTimeZone)
                ?? LocalTimes.FindZone(command.DefaultTimeZone) ?? TimeZoneInfo.Utc;
            var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
            if (local.DayOfWeek != DayOfWeek.Monday || local.Hour < WeeklyDigests.PostFromHour)
                continue;
            var thisWeek = WeeklyDigests.WeekStart(local);
            var lastWeek = thisWeek.AddDays(-7);
            DateTimeOffset from = LocalTimes.StartOfDay(lastWeek, zone), until = LocalTimes.StartOfDay(thisWeek, zone);

            var gameLists = (await groups.ListGamesAsync(guildId)).ToLookup(game => game.GroupId);
            foreach (var group in await groups.ListAsync(guildId))
            {
                var games = GameStats.GamesOf(gameLists[group.Id]);
                if (games.Count == 0 || !await store.GetDigestChannelAsync(guildId, group.Id))
                    continue;
                // Claimed before building, so two runs on the same Monday never post twice.
                if (!await store.MarkDigestAsync(guildId, group.Id, lastWeek))
                    continue;
                var members = group.Members.Append(group.OrganizerId).Distinct().Where(sharers.Contains).ToArray();
                var matches = (await store.ListMatchesAsync(members, from)).Where(match => match.StartedAt < until);
                var digests = WeeklyDigests.Build(MatchRecaps.Find(group.Id, members, matches, games));
                if (digests.Count == 0)
                    continue;
                foreach (var publisher in publishers)
                    await publisher.PublishDigestAsync(guildId, group.Id, group.Name, lastWeek, digests, cancellationToken);
                posted++;
            }
        }
        return posted;
    }
}
