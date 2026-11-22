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

/// <summary>One member's card: whether they share, their Steam account and the cached Dota 2 / CS2 results.</summary>
public sealed record PlayerStatsCard(ulong UserId, bool Shares, string? SteamId, CachedStats? Dota, CachedStats? Cs2)
{
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
    public int Sharing => Cards.Count(card => card.Shares);
    /// <summary>Matches two or more sharing members played together in the last two weeks, newest first.</summary>
    public IReadOnlyList<GroupRecap> Recaps { get; init; } = [];
    /// <summary>Whether recaps are also posted in the group's notification channel.</summary>
    public bool PostRecaps { get; init; }
    /// <summary>The last 7 days, one summary per game with games together.</summary>
    public IReadOnlyList<GroupDigest> Week { get; init; } = [];
    /// <summary>Whether the Monday summary is posted in the group's notification channel.</summary>
    public bool PostDigest { get; init; }
    public bool CanManage { get; init; }
}

public sealed record GetGroupStatsQuery(GroupActor Actor, Guid GroupId) : IQuery<GroupStats>;

/// <summary>The viewer's own sharing choice and cards, for My games and <c>/stats</c>.</summary>
public sealed record MyStats(bool Shares, string? SteamId, CachedStats? Dota, CachedStats? Cs2, bool Cs2Enabled);

public sealed record GetMyStatsQuery(ulong UserId) : IQuery<MyStats>;

/// <summary>
/// Turns stats sharing on or off for the person's Discord account (all their groups). Turning it on fetches their
/// cards straight away; turning it off deletes the cached cards.
/// </summary>
/// <param name="GuildId">The server the change came from, so lookup problems show in that server's Logs.</param>
public sealed record SetStatsSharingCommand(ulong UserId, bool Share, ulong? GuildId = null) : ICommand<MyStats>;

/// <summary>Refreshes the oldest stale cards of people who share. Returns how many cards were fetched.</summary>
public sealed record RefreshGameStatsCommand(int Limit = 20) : ICommand<int>;

/// <summary>"Refresh" on the Stats tab or the Game features page: the viewer's own cards and matches right now.</summary>
public sealed record RefreshMyStatsCommand(ulong UserId, ulong? GuildId = null) : ICommand<MyStats>;

/// <summary>
/// "Refresh" on a group's Stats tab: cards and matches of the group's sharing members that are older than
/// <see cref="GameStats.RefreshAfter"/>, up to <see cref="GameStats.RefreshAtOnce"/> people, so the services aren't flooded.
/// Returns how many people were refreshed.
/// </summary>
public sealed record RefreshGroupStatsCommand(GroupActor Actor, Guid GroupId) : ICommand<int>;

public static class GameStats
{
    public const string DotaAppId = "570";
    public const string Cs2AppId = "730";
    /// <summary>How long a card counts as fresh.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
    /// <summary>A Refresh click skips cards fetched more recently than this.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);
    public const int RefreshAtOnce = 8;
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
    ICommandHandler<RefreshGameStatsCommand, int>, ICommandHandler<RefreshMyStatsCommand, MyStats>, ICommandHandler<RefreshGroupStatsCommand, int>
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
        var cached = (await store.ListCachedAsync(members)).ToLookup(item => item.UserId);
        var cards = new List<PlayerStatsCard>();
        foreach (ulong member in members)
        {
            bool shares = sharing.Contains(member);
            string? steamId = shares ? (await players.GetSteamAsync(member))?.SteamId : null;
            cards.Add(new PlayerStatsCard(member, shares, steamId,
                shares ? cached[member].FirstOrDefault(item => item.Game == StatsGame.Dota2) : null,
                shares ? cached[member].FirstOrDefault(item => item.Game == StatsGame.Cs2) : null));
        }

        // Sharing and Steam belong to the Discord account itself, like My games.
        ulong viewer = query.Actor.UserId;
        bool isMember = links.Includes(detail.Members, query.Actor.UserId) || links.Same(detail.Summary.OrganizerId, query.Actor.UserId);
        var now = clock.UtcNow;
        var sharers = cards.Where(card => card.Shares).Select(card => card.UserId).ToArray();
        var together = MatchRecaps.Find(query.GroupId, sharers, await store.ListMatchesAsync(sharers, now - MatchRecaps.ShowFor), games);
        var recaps = together.Take(MatchRecaps.MaxShown).ToArray();
        return new GroupStats(games, cards, (await store.ListSharingAsync([viewer])).Contains(viewer), isMember,
            await players.GetSteamAsync(viewer) is not null, directory.Cs2Enabled, now)
        {
            Recaps = recaps,
            PostRecaps = await store.GetRecapChannelAsync(query.Actor.GuildId, query.GroupId),
            Week = WeeklyDigests.Build(together.Where(recap => now - recap.StartedAt < TimeSpan.FromDays(7))),
            PostDigest = await store.GetDigestChannelAsync(query.Actor.GuildId, query.GroupId),
            CanManage = detail.Summary.CanManage
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
        }
        else if (await players.GetSteamAsync(command.UserId) is { } steam)
            await FetchAsync(command.UserId, steam.SteamId, command.GuildId, cancellationToken);
        return await MineAsync(command.UserId);
    }

    public async Task<Result<MyStats>> Handle(RefreshMyStatsCommand command, CancellationToken cancellationToken)
    {
        if ((await store.ListSharingAsync([command.UserId])).Contains(command.UserId) && await players.GetSteamAsync(command.UserId) is { } steam)
        {
            await FetchAsync(command.UserId, steam.SteamId, command.GuildId, cancellationToken);
            await sender.Send(new RefreshPlayerMatchesCommand(command.UserId, command.GuildId), cancellationToken);
        }
        return await MineAsync(command.UserId);
    }

    public async Task<Result<int>> Handle(RefreshGroupStatsCommand command, CancellationToken cancellationToken)
    {
        if (await sender.Send(new GetGroupDetailQuery(command.Actor, command.GroupId), cancellationToken) is not { IsSuccess: true, Value: { CanViewRoster: true } detail })
            return Result.Failure<int>(NotFound);
        var now = clock.UtcNow;
        var members = detail.Members.Append(detail.Summary.OrganizerId).Distinct().ToArray();
        var sharing = await store.ListSharingAsync(members);
        var cached = (await store.ListCachedAsync(members)).ToLookup(item => item.UserId);
        // The viewer first, then the oldest cards; anything fetched in the last few minutes is skipped.
        var due = members.Where(sharing.Contains)
            .Select(user => (User: user, Last: cached[user].Select(item => (DateTimeOffset?)item.FetchedAt).Min()))
            .Where(item => item.Last is null || now - item.Last >= GameStats.RefreshAfter)
            .OrderBy(item => item.User == command.Actor.UserId ? 0 : 1).ThenBy(item => item.Last ?? DateTimeOffset.MinValue)
            .Take(GameStats.RefreshAtOnce).ToArray();
        int refreshed = 0;
        foreach (var (user, _) in due)
        {
            if (await players.GetSteamAsync(user) is not { } steam)
                continue;
            await FetchAsync(user, steam.SteamId, command.Actor.GuildId, cancellationToken);
            await sender.Send(new RefreshPlayerMatchesCommand(user, command.Actor.GuildId), cancellationToken);
            refreshed++;
        }
        return refreshed;
    }

    public async Task<Result<int>> Handle(RefreshGameStatsCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sharers = await store.ListAllSharingAsync();
        var cached = (await store.ListCachedAsync(sharers)).ToLookup(item => item.UserId);
        // Oldest first; a person with no card yet counts as oldest.
        var due = sharers.Select(user => (User: user, Oldest: cached[user].Select(item => (DateTimeOffset?)item.FetchedAt).Min()))
            .Where(item => GameStats.IsStale(cached[item.User].FirstOrDefault(card => card.Game == StatsGame.Dota2), now)
                || directory.Cs2Enabled && GameStats.IsStale(cached[item.User].FirstOrDefault(card => card.Game == StatsGame.Cs2), now))
            .OrderBy(item => item.Oldest ?? DateTimeOffset.MinValue)
            .Take(Math.Max(1, command.Limit))
            .ToArray();
        int fetched = 0;
        foreach (var (user, _) in due)
        {
            if (await players.GetSteamAsync(user) is not { } steam)
                continue;
            fetched += await FetchAsync(user, steam.SteamId, null, cancellationToken);
        }
        return fetched;
    }

    /// <summary>Fetches both cards. A failed fetch keeps the previous good card's data and only marks the error.</summary>
    private async Task<int> FetchAsync(ulong userId, string steamId, ulong? guildId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var previous = (await store.ListCachedAsync([userId])).ToArray();
        int fetched = 0;
        var dota = await directory.GetDotaAsync(steamId, cancellationToken);
        await SaveAsync(userId, guildId, StatsGame.Dota2, dota, previous, now);
        fetched++;
        if (directory.Cs2Enabled)
        {
            var cs2 = await directory.GetCs2Async(steamId, cancellationToken);
            await SaveAsync(userId, guildId, StatsGame.Cs2, cs2, previous, now);
            fetched++;
        }
        return fetched;
    }

    private async Task SaveAsync<T>(ulong userId, ulong? guildId, StatsGame game, StatsFetch<T> fetch, IReadOnlyList<CachedStats> previous, DateTimeOffset now)
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
        await store.SaveCachedAsync(new CachedStats(userId, game, fetch.Status, payload, now, fetch.Status == StatsStatus.Ok ? null : fetch.Reason));
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
        return new MyStats((await store.ListSharingAsync([userId])).Contains(userId), (await players.GetSteamAsync(userId))?.SteamId,
            cached.FirstOrDefault(item => item.Game == StatsGame.Dota2), cached.FirstOrDefault(item => item.Game == StatsGame.Cs2), directory.Cs2Enabled);
    }
}
