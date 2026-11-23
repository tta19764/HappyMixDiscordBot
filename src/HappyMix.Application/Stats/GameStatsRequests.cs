using System.Globalization;
using System.Text.Json;
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
/// One member's card for one Steam account: whether they share, the account and the cached Dota 2 / CS2 results. A member
/// who shows several accounts (twinks) has one card per account.
/// </summary>
public sealed record PlayerStatsCard(ulong UserId, bool Shares, string? SteamId, CachedStats? Dota, CachedStats? Cs2)
{
    /// <summary>Whether the card is for the member's main Steam account.</summary>
    public bool IsMain { get; init; } = true;
    /// <summary>The member's name for the account, if they gave it one.</summary>
    public string? Label { get; init; }
    public DotaProfile? DotaProfile => GameStats.Read<DotaProfile>(Dota);
    public Cs2Profile? Cs2Profile => GameStats.Read<Cs2Profile>(Cs2);
    public CachedStats? For(StatsGame game) => game == StatsGame.Dota2 ? Dota : Cs2;
}

/// <param name="Games">The card games this group plays, Dota 2 first.</param>
/// <param name="IsMember">Whether the viewer can share here (members and the organizer).</param>
/// <param name="SteamLinked">Whether the viewer has linked Steam on My games.</param>
public sealed record GroupStats(IReadOnlyList<StatsGame> Games, IReadOnlyList<PlayerStatsCard> Cards, bool YouShare, bool IsMember, bool SteamLinked,
    bool Cs2Enabled, DateTimeOffset Now)
{
    /// <summary>Members who share (a member with twink cards counts once).</summary>
    public int Sharing => Cards.Where(card => card.Shares).Select(card => card.UserId).Distinct().Count();
    public int MemberCount => Cards.Select(card => card.UserId).Distinct().Count();
    /// <summary>Matches two or more sharing members played together in the last two weeks, newest first.</summary>
    public IReadOnlyList<GroupRecap> Recaps { get; init; } = [];
    /// <summary>Whether recaps are also posted in the group's notification channel.</summary>
    public bool PostRecaps { get; init; }
    /// <summary>The last 7 days, one summary per game with games together.</summary>
    public IReadOnlyList<GroupDigest> Week { get; init; } = [];
    /// <summary>Whether the Monday summary is posted in the group's notification channel.</summary>
    public bool PostDigest { get; init; }
    public bool CanManage { get; init; }
    /// <summary>Pairs of sharing members and their Dota 2 games on the same team (OpenDota peers, all time), most games first.</summary>
    public IReadOnlyList<DotaDuo> Duos { get; init; } = [];
    /// <summary>Per sharing member: matches with at least one other member over <see cref="MatchRecaps.ShowFor"/>, per game.</summary>
    public IReadOnlyDictionary<(ulong UserId, StatsGame Game), WinRecord> WithGroup { get; init; } = new Dictionary<(ulong, StatsGame), WinRecord>();
}

public sealed record DotaDuo(ulong First, ulong Second, int Games, int Wins);

public sealed record WinRecord(int Games, int Wins);

public sealed record GetGroupStatsQuery(GroupActor Actor, Guid GroupId) : IQuery<GroupStats>;

/// <summary>
/// The viewer's own sharing choice and cards, for My games and <c>/stats</c>. <see cref="SteamId"/>, <see cref="Dota"/>
/// and <see cref="Cs2"/> are the first shown account's (or the main account's); <see cref="Accounts"/> has every account.
/// </summary>
public sealed record MyStats(bool Shares, string? SteamId, CachedStats? Dota, CachedStats? Cs2, bool Cs2Enabled)
{
    public IReadOnlyList<AccountStats> Accounts { get; init; } = [];
}

/// <summary>One linked Steam account with whether its stats are shown and its cards.</summary>
public sealed record AccountStats(SteamAccount Account, CachedStats? Dota, CachedStats? Cs2)
{
    public string SteamId => Account.SteamId;
    public bool Show => Account.ShowStats;
}

/// <summary>
/// Chooses which of the person's Steam accounts show stats to their groups: some, all or none. None turns sharing off and
/// deletes the saved stats; newly shown accounts are fetched straight away and hidden ones' cards are deleted.
/// </summary>
public sealed record SetStatsAccountsCommand(ulong UserId, IReadOnlyCollection<string> SteamIds, ulong? GuildId = null) : ICommand<MyStats>;

public sealed record GetMyStatsQuery(ulong UserId) : IQuery<MyStats>;

/// <summary>
/// Turns stats sharing on or off for the person's Discord account (all their groups). Turning it on fetches their
/// cards straight away; turning it off deletes the cached cards.
/// </summary>
/// <param name="GuildId">The server the change came from, so lookup problems show in that server's Logs.</param>
public sealed record SetStatsSharingCommand(ulong UserId, bool Share, ulong? GuildId = null) : ICommand<MyStats>;

/// <summary>
/// The bot's own refresh (every 10 minutes): the oldest cards of people who share that are older than
/// <see cref="GameStats.FreshFor"/>, so everyone's cards are about an hour old at most. Returns how many cards were fetched.
/// </summary>
public sealed record RefreshGameStatsCommand(int Limit = 20) : ICommand<int>;

/// <summary>
/// "Refresh me" on a group's Stats tab or the Game features page: the viewer's own cards and matches right now. Cards that
/// loaded fine less than <see cref="GameStats.RefreshAfter"/> ago are kept, so repeated clicks don't call the services again.
/// </summary>
public sealed record RefreshMyStatsCommand(ulong UserId, ulong? GuildId = null) : ICommand<MyStats>;


public static class GameStats
{
    public const string DotaAppId = "570";
    public const string Cs2AppId = "730";
    /// <summary>How long a card counts as fresh: the bot refreshes older cards on its own, so they update every hour.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);
    /// <summary>"Refresh me" keeps cards that loaded fine more recently than this.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(1);
    /// <summary>SteamID64 minus this is Dota's 32-bit account id (used by OpenDota and Dotabuff).</summary>
    public const long SteamIdBase = 76561197960265728;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The card games a group plays, from its Steam games.</summary>
    public static IReadOnlyList<StatsGame> GamesOf(IEnumerable<GroupGame> games)
    {
        var appIds = games.Select(game => game.SteamAppId).ToHashSet(StringComparer.Ordinal);
        var result = new List<StatsGame>();
        if (appIds.Contains(DotaAppId)) result.Add(StatsGame.Dota2);
        if (appIds.Contains(Cs2AppId)) result.Add(StatsGame.Cs2);
        return result;
    }

    public static long? DotaAccountId(string? steamId64) =>
        long.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > SteamIdBase ? id - SteamIdBase : null;

    /// <summary>Medal index 1 (Herald) … 8 (Immortal) and stars 0–5, or null when unranked.</summary>
    public static (int Medal, int Stars)? DotaMedal(int? rankTier) =>
        rankTier is >= 10 and <= 85 and var tier && tier / 10 is >= 1 and <= 8 ? (tier / 10, tier % 10) : null;

    /// <summary>Roughly how much MMR one ranked game moves (Dota 2 doesn't make the real number public).</summary>
    public const int MmrPerGame = 25;
    /// <summary>How long the bot keeps a member's daily medal.</summary>
    public const int RankHistoryDays = 90;

    /// <summary>The medal as a count of stars from Herald 0: each medal is 5 stars, so a change reads as "+2 stars".</summary>
    public static int? StarsOf(int? rankTier) => DotaMedal(rankTier) is var (medal, stars) ? (medal - 1) * 5 + Math.Min(stars, 5) : null;

    /// <summary>
    /// The fresh card with the medal history carried over from the previous card: one point per day, the latest of the
    /// day wins, kept for <see cref="RankHistoryDays"/>.
    /// </summary>
    public static DotaProfile WithRankHistory(DotaProfile fresh, DotaProfile? previous, DateOnly today)
    {
        var points = (previous?.RankHistory ?? []).Where(point => point.Day > today.AddDays(-RankHistoryDays) && point.Day < today).ToList();
        if (fresh.RankTier is { } tier)
            points.Add(new DotaRankPoint(today, tier));
        return fresh with { RankHistory = points.OrderBy(point => point.Day).ToArray() };
    }

    /// <summary>
    /// Stars gained (or lost) since the oldest saved medal within <paramref name="days"/> days, and that day. Null when
    /// unranked or there's no earlier day to compare with yet.
    /// </summary>
    public static (int Stars, DateOnly Since)? RankChange(DotaProfile profile, DateOnly today, int days = 30)
    {
        if (StarsOf(profile.RankTier) is not { } now)
            return null;
        var first = profile.RankHistory.FirstOrDefault(point => point.Day >= today.AddDays(-days) && point.Day < today && StarsOf(point.RankTier) is not null);
        return first is null ? null : (now - StarsOf(first.RankTier)!.Value, first.Day);
    }

    /// <summary>An estimate from ranked wins minus losses in the last matches; null without ranked games.</summary>
    public static int? MmrEstimate(DotaRecent? recent) => recent is { RankedGames: > 0 } ranked ? (2 * ranked.RankedWins - ranked.RankedGames) * MmrPerGame : null;

    /// <summary>Pairs of sharing members found in each other's OpenDota peers. Both sides list the same games; the larger count wins.</summary>
    public static IReadOnlyList<DotaDuo> Duos(IEnumerable<PlayerStatsCard> cards)
    {
        var sharing = cards.Where(card => card.Shares && card.DotaProfile is not null).ToArray();
        var userOf = new Dictionary<long, ulong>();
        foreach (var card in sharing)
            if (DotaAccountId(card.SteamId) is { } account)
                userOf.TryAdd(account, card.UserId);
        var pairs = new Dictionary<(ulong, ulong), DotaDuo>();
        foreach (var card in sharing)
            foreach (var peer in card.DotaProfile!.Peers)
            {
                if (!userOf.TryGetValue(peer.AccountId, out ulong other) || other == card.UserId)
                    continue;
                var key = card.UserId < other ? (card.UserId, other) : (other, card.UserId);
                if (!pairs.TryGetValue(key, out var known) || known.Games < peer.Games)
                    pairs[key] = new DotaDuo(key.Item1, key.Item2, peer.Games, peer.Wins);
            }
        return pairs.Values.OrderByDescending(duo => duo.Games).ThenByDescending(duo => duo.Wins).ToArray();
    }

    /// <summary>Per member and game: recaps they were in, and how many their side won.</summary>
    public static Dictionary<(ulong UserId, StatsGame Game), WinRecord> WithGroup(IEnumerable<GroupRecap> recaps)
    {
        var result = new Dictionary<(ulong, StatsGame), WinRecord>();
        foreach (var recap in recaps)
            foreach (var player in recap.Players.DistinctBy(player => player.UserId))
            {
                var known = result.GetValueOrDefault((player.UserId, recap.Game)) ?? new WinRecord(0, 0);
                result[(player.UserId, recap.Game)] = new WinRecord(known.Games + 1, known.Wins + (player.Won ? 1 : 0));
            }
        return result;
    }

    public static T? Read<T>(CachedStats? stats) where T : class
    {
        if (stats?.Payload is not { Length: > 0 } payload)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsStale(CachedStats? stats, DateTimeOffset now) => stats is null || now - stats.FetchedAt >= FreshFor;
}

internal sealed partial class GameStatsHandlers(IGameStatsStore store, IGameStatsDirectory directory, IPlayerGameStore players, IAccountLinkStore accountLinks,
    IClock clock, ISender sender, Microsoft.Extensions.Logging.ILogger<GameStatsHandlers> logger) :
    IQueryHandler<GetGroupStatsQuery, GroupStats>, IQueryHandler<GetMyStatsQuery, MyStats>, ICommandHandler<SetStatsSharingCommand, MyStats>,
    ICommandHandler<RefreshGameStatsCommand, int>, ICommandHandler<RefreshMyStatsCommand, MyStats>, ICommandHandler<SetStatsAccountsCommand, MyStats>
{
    private static readonly ErrorDescriptor NotFound = new("Stats.NotFound", "That group doesn't exist or you don't have access to it.");

    public async Task<Result<GroupStats>> Handle(GetGroupStatsQuery query, CancellationToken cancellationToken)
    {
        if (await sender.Send(new GetGroupDetailQuery(query.Actor, query.GroupId), cancellationToken) is not { IsSuccess: true, Value: { CanViewRoster: true } detail })
            return Result.Failure<GroupStats>(NotFound);
        var games = GameStats.GamesOf(detail.Games);
        if (games.Count == 0)
            return Result.Failure<GroupStats>(NotFound);

        var links = await accountLinks.LoadAsync(query.Actor.GuildId);
        var members = links.Representatives(detail.Members.Append(detail.Summary.OrganizerId)).ToArray();
        var sharing = await store.ListSharingAsync(members);
        var cached = (await store.ListCachedAsync(members)).ToLookup(item => (item.UserId, item.SteamId));
        var cards = new List<PlayerStatsCard>();
        foreach (ulong member in members)
        {
            // One card per shown account (main first); a member who doesn't share, or shows no account, gets one empty card.
            var shown = sharing.Contains(member) ? await ShownAsync(member) : [];
            if (shown.Count == 0)
                cards.Add(new PlayerStatsCard(member, false, null, null, null));
            foreach (var account in shown)
                cards.Add(new PlayerStatsCard(member, true, account.SteamId,
                    cached[(member, account.SteamId)].FirstOrDefault(item => item.Game == StatsGame.Dota2),
                    cached[(member, account.SteamId)].FirstOrDefault(item => item.Game == StatsGame.Cs2))
                { IsMain = account.IsMain, Label = account.Label });
        }

        // Sharing and Steam belong to the Discord account itself, like My games.
        ulong viewer = query.Actor.UserId;
        bool isMember = links.Includes(detail.Members, query.Actor.UserId) || links.Same(detail.Summary.OrganizerId, query.Actor.UserId);
        var now = clock.UtcNow;
        var sharers = cards.Where(card => card.Shares).Select(card => card.UserId).Distinct().ToArray();
        var together = MatchRecaps.Find(query.GroupId, sharers, await store.ListMatchesAsync(sharers, now - MatchRecaps.ShowFor), games);
        var shownRecaps = together.Take(MatchRecaps.MaxShown).ToArray();
        var recaps = MatchRecaps.WithDetails(shownRecaps,
            await store.ListMatchDetailsAsync(shownRecaps.Select(recap => (recap.Game, recap.MatchId)).ToArray()));
        return new GroupStats(games, cards, (await store.ListSharingAsync([viewer])).Contains(viewer), isMember,
            await players.GetSteamAsync(viewer) is not null, directory.Cs2Enabled, now)
        {
            Recaps = recaps,
            PostRecaps = await store.GetRecapChannelAsync(query.Actor.GuildId, query.GroupId),
            Week = WeeklyDigests.Build(together.Where(recap => now - recap.StartedAt < TimeSpan.FromDays(7))),
            PostDigest = await store.GetDigestChannelAsync(query.Actor.GuildId, query.GroupId),
            CanManage = detail.Summary.CanManage,
            Duos = GameStats.Duos(cards),
            WithGroup = GameStats.WithGroup(together)
        };
    }

    public async Task<Result<MyStats>> Handle(GetMyStatsQuery query, CancellationToken cancellationToken) => await MineAsync(query.UserId);

    public async Task<Result<MyStats>> Handle(SetStatsSharingCommand command, CancellationToken cancellationToken)
    {
        await store.SetSharingAsync(command.UserId, command.Share, clock.UtcNow);
        if (!command.Share)
        {
            await store.RemoveCachedAsync(command.UserId);
            await store.RemoveMatchesAsync(command.UserId);
            return await MineAsync(command.UserId);
        }
        // Turning sharing on shows the accounts chosen before, or every account when none was chosen.
        var accounts = await players.ListSteamAsync(command.UserId);
        if (accounts.Count > 0 && accounts.All(account => !account.ShowStats))
            foreach (var account in accounts)
                await players.SaveSteamAsync(account with { ShowStats = true });
        foreach (var account in await ShownAsync(command.UserId))
            await FetchAsync(command.UserId, account.SteamId, command.GuildId, cancellationToken);
        return await MineAsync(command.UserId);
    }

    public async Task<Result<MyStats>> Handle(SetStatsAccountsCommand command, CancellationToken cancellationToken)
    {
        var accounts = await players.ListSteamAsync(command.UserId);
        var chosen = command.SteamIds.Where(id => accounts.Any(account => account.SteamId == id)).ToHashSet(StringComparer.Ordinal);
        if (chosen.Count == 0)
        {
            // None: sharing off, like the switch.
            await store.SetSharingAsync(command.UserId, false, clock.UtcNow);
            await store.RemoveCachedAsync(command.UserId);
            await store.RemoveMatchesAsync(command.UserId);
            foreach (var account in accounts.Where(account => account.ShowStats))
                await players.SaveSteamAsync(account with { ShowStats = false });
            return await MineAsync(command.UserId);
        }
        bool wasSharing = (await store.ListSharingAsync([command.UserId])).Contains(command.UserId);
        await store.SetSharingAsync(command.UserId, true, clock.UtcNow);
        foreach (var account in accounts)
        {
            bool show = chosen.Contains(account.SteamId);
            if (account.ShowStats != show)
                await players.SaveSteamAsync(account with { ShowStats = show });
            if (!show)
                await store.RemoveCachedAsync(command.UserId, account.SteamId);
            else if (!wasSharing || !account.ShowStats)
                await FetchAsync(command.UserId, account.SteamId, command.GuildId, cancellationToken);
        }
        return await MineAsync(command.UserId);
    }

    /// <summary>The person's accounts whose stats are shown, main first.</summary>
    private async Task<IReadOnlyList<SteamAccount>> ShownAsync(ulong userId) =>
        (await players.ListSteamAsync(userId)).Where(account => account.ShowStats).ToArray();

    public async Task<Result<MyStats>> Handle(RefreshMyStatsCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (!(await store.ListSharingAsync([command.UserId])).Contains(command.UserId))
            return await MineAsync(command.UserId);
        var cards = await store.ListCachedAsync([command.UserId]);
        bool any = false;
        foreach (var account in await ShownAsync(command.UserId))
        {
            var mine = cards.Where(card => card.SteamId == account.SteamId).ToArray();
            bool justLoaded = mine.Length > 0 && mine.All(card => card.Status != StatsStatus.Error && now - card.FetchedAt < GameStats.RefreshAfter);
            if (justLoaded)
                continue;
            await FetchAsync(command.UserId, account.SteamId, command.GuildId, cancellationToken);
            any = true;
        }
        if (any)
            await sender.Send(new RefreshPlayerMatchesCommand(command.UserId, command.GuildId), cancellationToken);
        return await MineAsync(command.UserId);
    }

    public async Task<Result<int>> Handle(RefreshGameStatsCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sharers = await store.ListAllSharingAsync();
        var cached = (await store.ListCachedAsync(sharers)).ToLookup(item => (item.UserId, item.SteamId));
        // Every shown account of every sharer, oldest card first; an account with no card yet counts as oldest.
        var accounts = new List<(ulong User, string SteamId, DateTimeOffset? Oldest)>();
        foreach (ulong user in sharers)
            foreach (var account in await ShownAsync(user))
            {
                var cards = cached[(user, account.SteamId)];
                if (GameStats.IsStale(cards.FirstOrDefault(card => card.Game == StatsGame.Dota2), now)
                    || directory.Cs2Enabled && GameStats.IsStale(cards.FirstOrDefault(card => card.Game == StatsGame.Cs2), now))
                    accounts.Add((user, account.SteamId, cards.Select(card => (DateTimeOffset?)card.FetchedAt).Min()));
            }
        int fetched = 0;
        foreach (var (user, steamId, _) in accounts.OrderBy(item => item.Oldest ?? DateTimeOffset.MinValue).Take(Math.Max(1, command.Limit)))
        {
            // Without an OpenDota key the day's budget is shared with Refresh clicks; the rest waits for tomorrow.
            if (!directory.BackgroundBudgetLeft)
                break;
            fetched += await FetchAsync(user, steamId, null, cancellationToken);
        }
        return fetched;
    }

    /// <summary>Fetches both cards. A failed fetch keeps the previous good card's data and only marks the error.</summary>
    private async Task<int> FetchAsync(ulong userId, string steamId, ulong? guildId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var previous = (await store.ListCachedAsync([userId])).Where(item => item.SteamId == steamId).ToArray();
        int fetched = 0;
        var dota = await directory.GetDotaAsync(steamId, cancellationToken);
        // OpenDota only knows today's medal: the history of earlier days is carried over from the previous card.
        if (dota.Profile is { } fresh)
            dota = dota with
            {
                Profile = GameStats.WithRankHistory(fresh, GameStats.Read<DotaProfile>(previous.FirstOrDefault(item => item.Game == StatsGame.Dota2)),
                    DateOnly.FromDateTime(now.UtcDateTime))
            };
        await SaveAsync(userId, steamId, guildId, StatsGame.Dota2, dota, previous, now);
        fetched++;
        if (directory.Cs2Enabled)
        {
            var cs2 = await directory.GetCs2Async(steamId, cancellationToken);
            await SaveAsync(userId, steamId, guildId, StatsGame.Cs2, cs2, previous, now);
            fetched++;
        }
        return fetched;
    }

    private async Task SaveAsync<T>(ulong userId, string steamId, ulong? guildId, StatsGame game, StatsFetch<T> fetch, IReadOnlyList<CachedStats> previous, DateTimeOffset now)
        where T : class
    {
        string? payload = fetch.Profile is null ? null : JsonSerializer.Serialize(fetch.Profile, GameStats.Json);
        if (fetch.Status == StatsStatus.Error)
            payload = previous.FirstOrDefault(item => item.Game == game)?.Payload;
        // Errors are warnings; "no public data" and "no FACEIT account" are the player's own settings, so information.
        // The server id goes in a scope, so the entry shows in that server's Logs (without one it is bot-wide).
        using (guildId is { } guild ? logger.BeginScope(new Dictionary<string, object> { ["GuildId"] = guild }) : null)
        {
            if (fetch.Status == StatsStatus.Error)
                LogStatsFailed(logger, game, userId, fetch.Reason ?? "unknown");
            else if (fetch.Status != StatsStatus.Ok)
                LogStatsMissing(logger, game, userId, fetch.Reason ?? "no data");
        }
        await store.SaveCachedAsync(new CachedStats(userId, game, fetch.Status, payload, now, fetch.Status == StatsStatus.Ok ? null : fetch.Reason)
        {
            SteamId = steamId
        });
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "{Game} stats for {UserId} couldn't be loaded: {Reason}")]
    private static partial void LogStatsFailed(Microsoft.Extensions.Logging.ILogger logger, StatsGame game, ulong userId, string reason);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "{Game} stats for {UserId} have no data: {Reason}")]
    private static partial void LogStatsMissing(Microsoft.Extensions.Logging.ILogger logger, StatsGame game, ulong userId, string reason);

    private async Task<MyStats> MineAsync(ulong userId)
    {
        var cached = await store.ListCachedAsync([userId]);
        var accounts = (await players.ListSteamAsync(userId)).Select(account => new AccountStats(account,
            cached.FirstOrDefault(item => item.SteamId == account.SteamId && item.Game == StatsGame.Dota2),
            cached.FirstOrDefault(item => item.SteamId == account.SteamId && item.Game == StatsGame.Cs2))).ToArray();
        bool shares = (await store.ListSharingAsync([userId])).Contains(userId);
        // The single-card fields (Discord /stats, older callers) follow the first shown account, or the main one.
        var first = accounts.FirstOrDefault(account => shares && account.Show) ?? accounts.FirstOrDefault();
        return new MyStats(shares, first?.SteamId, first?.Dota, first?.Cs2, directory.Cs2Enabled) { Accounts = accounts };
    }
}
