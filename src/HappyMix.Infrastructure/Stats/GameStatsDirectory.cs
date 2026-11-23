using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Stats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HappyMix.Infrastructure.Stats;

/// <summary>
/// Public player stats. Dota 2 comes from OpenDota (no key needed; <c>OpenDota:ApiKey</c> raises its limits), CS2 from
/// FACEIT's Data API (needs <c>Faceit:ApiKey</c>). Every lookup is best effort: errors come back as
/// <see cref="StatsStatus.Error"/> and are logged, never thrown.
/// </summary>
internal sealed partial class GameStatsDirectory(IHttpClientFactory clients, IConfiguration configuration, ILogger<GameStatsDirectory> logger)
    : IGameStatsDirectory, IDotaMetaSource, IHeroMetaSource, IDisposable
{
    public const string StratzClient = "Stratz";
    private const string Stratz = "https://api.stratz.com/graphql";
    private static readonly TimeSpan PatchFor = TimeSpan.FromHours(6);
    private (string Name, DateTimeOffset Date)? patch;
    private DateTimeOffset patchAt;
    public const string OpenDotaClient = "OpenDota";
    /// <summary>How long one OpenDota call may take: its match endpoints often need more than 10 seconds.</summary>
    public const int OpenDotaSeconds = 45;
    /// <summary>
    /// OpenDota allows 2,000 calls a day without a key. Scheduled refreshes stop at this many calls (per UTC day) so
    /// Refresh clicks, match recaps and the hero list keep working.
    /// </summary>
    public const int FreeBackgroundCalls = 1500;
    private readonly Lock callsLock = new();
    /// <summary>Requests on their way, by URL (or STRATZ query): an identical request waits for the same answer instead of asking again.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> inFlight = new(StringComparer.Ordinal);
    /// <summary>Ranked matchmaking: the card counts only these games.</summary>
    internal const int RankedLobby = 7;
    private const string Ranked = "lobby_type=7";
    /// <summary>OpenDota's player matches, ranked only, with the fields the card reads.</summary>
    internal const string RankedMatchesQuery = Ranked + "&limit=20&project=kills&project=deaths&project=assists&project=gold_per_min"
        + "&project=xp_per_min&project=lane_role&project=is_roaming&project=party_size&project=game_mode&project=lobby_type"
        + "&project=radiant_win&project=player_slot&project=start_time&project=hero_id";
    private DateOnly callsDay;
    private int callsToday;
    public const string FaceitClient = "Faceit";
    private const string OpenDota = "https://api.opendota.com/api/";
    private const string Faceit = "https://open.faceit.com/data/v4/";
    private const string SteamCdn = "https://cdn.cloudflare.steamstatic.com";
    private static readonly TimeSpan HeroesFor = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim heroesLock = new(1);
    private IReadOnlyDictionary<int, (string Name, string? Image)>? heroes;
    private DateTimeOffset heroesAt;

    private string? FaceitKey => configuration["Faceit:ApiKey"] is { Length: > 0 } key ? key : null;
    private string? OpenDotaKey => configuration["OpenDota:ApiKey"] is { Length: > 0 } key ? key : null;
    private string? StratzKey => configuration["Stratz:ApiKey"] is { Length: > 0 } key ? key : null;

    public bool Enabled => StratzKey is not null;

    public bool Cs2Enabled => FaceitKey is not null;

    public bool BackgroundBudgetLeft => OpenDotaKey is not null || OpenDotaCallsToday(0) < FreeBackgroundCalls;

    /// <summary>Adds <paramref name="added"/> calls to today's OpenDota count and returns the count.</summary>
    private int OpenDotaCallsToday(int added)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        lock (callsLock)
        {
            if (callsDay != today)
                (callsDay, callsToday) = (today, 0);
            return callsToday += added;
        }
    }

    public void Dispose() => heroesLock.Dispose();

    public async Task<StatsFetch<DotaProfile>> GetDotaAsync(string steamId64, CancellationToken cancellationToken)
    {
        if (GameStats.DotaAccountId(steamId64) is not { } accountId)
            return new(StatsStatus.NoAccount, null, "The Steam ID isn't a valid SteamID64.");
        try
        {
            string id = accountId.ToString(CultureInfo.InvariantCulture);
            // OpenDota's match endpoints can each take 10+ seconds, so every call starts at once. The card needs the
            // first three; the last matches, teammates and positions are extras that may fail without losing it.
            // Everything on the card counts ranked matchmaking games only.
            var playerTask = OpenDotaAsync($"players/{id}", null, cancellationToken);
            var recordTask = OpenDotaAsync($"players/{id}/wl", "date=30&" + Ranked, cancellationToken);
            var playedTask = OpenDotaAsync($"players/{id}/heroes", "date=90&" + Ranked, cancellationToken);
            var recentTask = ExtraAsync($"players/{id}/matches", RankedMatchesQuery, cancellationToken);
            var peersTask = ExtraAsync($"players/{id}/peers", Ranked, cancellationToken);
            var positionsTask = PositionsAsync(accountId, cancellationToken);
            var heroesTask = HeroesAsync(cancellationToken);
            using var recent = await recentTask;
            using var peers = await peersTask;
            var positions = await positionsTask;
            using var player = await Disposing(playerTask, recordTask, playedTask);
            using var record = await recordTask;
            using var played = await playedTask;
            var profile = ParseDota(accountId, player.RootElement, record.RootElement, played.RootElement, await heroesTask);
            if (profile is null)
                return new(StatsStatus.NoPublicData, null, $"OpenDota has no rank and no ranked matches for account {id}: \"Expose Public Match Data\" is probably off in Dota 2, or there were no ranked games in the last 90 days.");
            var (last, lanes) = recent is null ? (null, []) : ParseRecent(recent.RootElement);
            return new(StatsStatus.Ok, profile with
            {
                Recent = last,
                Roles = positions is { Count: > 0 } ? positions : lanes,
                RolesFrom = positions is { Count: > 0 } ? "STRATZ" : lanes.Count > 0 ? "OpenDota" : null,
                Peers = peers is null ? [] : ParsePeers(peers.RootElement)
            });
        }
        catch (Exception error) when (Reason("OpenDota", error) is { } reason)
        {
            LogLookupFailed(logger, "OpenDota", reason);
            return new(StatsStatus.Error, null, reason);
        }
    }

    /// <summary>
    /// Waits for all the needed answers and returns the first. When one fails, the others that did answer are disposed
    /// and the failure is thrown, so no document is left behind.
    /// </summary>
    private static async Task<JsonDocument> Disposing(Task<JsonDocument> first, params Task<JsonDocument>[] others)
    {
        Task<JsonDocument>[] all = [first, .. others];
        try
        {
            await Task.WhenAll(all);
        }
        catch
        {
            foreach (var task in all.Where(task => task.IsCompletedSuccessfully))
                task.Result.Dispose();
            throw;
        }
        return first.Result;
    }

    /// <summary>An OpenDota answer the card can do without: a failure is logged and gives null.</summary>
    private async Task<JsonDocument?> ExtraAsync(string path, string? query, CancellationToken cancellationToken)
    {
        try
        {
            return await OpenDotaAsync(path, query, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && Reason("OpenDota", error) is { } reason)
        {
            LogLookupFailed(logger, "OpenDota extras", reason);
            return null;
        }
    }

    /// <summary>Positions 1–5 from STRATZ when its key is set; null (lanes from OpenDota are used instead) when it isn't or fails.</summary>
    private async Task<IReadOnlyList<DotaRole>?> PositionsAsync(long accountId, CancellationToken cancellationToken)
    {
        if (StratzKey is not { } key)
            return null;
        try
        {
            using var document = await StratzAsync(key, StratzPositionsQuery(accountId), cancellationToken);
            return ParseStratzPositions(document.RootElement);
        }
        catch (Exception error) when (Reason("STRATZ", error) is { } reason)
        {
            LogLookupFailed(logger, "STRATZ positions", reason);
            return null;
        }
    }

    public async Task<StatsFetch<Cs2Profile>> GetCs2Async(string steamId64, CancellationToken cancellationToken)
    {
        if (FaceitKey is not { } key)
            return new(StatsStatus.NoAccount, null, "Faceit__ApiKey isn't set on the server, so CS2 stats are off.");
        try
        {
            using var client = clients.CreateClient(FaceitClient);
            using var player = await FaceitAsync(client, key, $"players?game=cs2&game_player_id={Uri.EscapeDataString(steamId64)}", true, cancellationToken);
            if (player is null || Text(player.RootElement, "player_id") is not { } faceitId)
                return new(StatsStatus.NoAccount, null, $"FACEIT has no CS2 account connected to Steam account {steamId64}.");
            // A FACEIT account that never played CS2 has no stats; its card still shows level and links.
            using var stats = await FaceitAsync(client, key, $"players/{Uri.EscapeDataString(faceitId)}/stats/cs2", true, cancellationToken);
            return new(StatsStatus.Ok, ParseCs2(player.RootElement, stats?.RootElement));
        }
        catch (Exception error) when (Reason("FACEIT", error) is { } reason)
        {
            LogLookupFailed(logger, "FACEIT", reason);
            return new(StatsStatus.Error, null, reason);
        }
    }

    public async Task<StatsFetch<IReadOnlyList<PlayerMatch>>> GetMatchesAsync(ulong userId, StatsGame game, string steamId64,
        IReadOnlySet<string> known, CancellationToken cancellationToken)
    {
        string service = game == StatsGame.Dota2 ? "OpenDota" : "FACEIT";
        try
        {
            if (game == StatsGame.Dota2)
            {
                if (GameStats.DotaAccountId(steamId64) is not { } accountId)
                    return new(StatsStatus.NoAccount, null, "The Steam ID isn't a valid SteamID64.");
                // Recaps of games together count every mode, not only ranked.
                using var recent = await OpenDotaAsync($"players/{accountId.ToString(CultureInfo.InvariantCulture)}/recentMatches", null, cancellationToken);
                return new(StatsStatus.Ok, ParseDotaMatches(userId, recent.RootElement, await HeroesAsync(cancellationToken)));
            }

            if (FaceitKey is not { } key)
                return new(StatsStatus.NoAccount, null, "Faceit__ApiKey isn't set on the server, so CS2 stats are off.");
            using var faceit = clients.CreateClient(FaceitClient);
            using var player = await FaceitAsync(faceit, key, $"players?game=cs2&game_player_id={Uri.EscapeDataString(steamId64)}", true, cancellationToken);
            if (player is null || Text(player.RootElement, "player_id") is not { } faceitId)
                return new(StatsStatus.NoAccount, null, $"FACEIT has no CS2 account connected to Steam account {steamId64}.");
            using var history = await FaceitAsync(faceit, key, $"players/{Uri.EscapeDataString(faceitId)}/history?game=cs2&limit=10", false, cancellationToken);
            var matches = new List<PlayerMatch>();
            foreach (var item in FaceitHistory(history!.RootElement).Where(item => !known.Contains(item.MatchId)))
            {
                // Each new match costs one call for the per-player numbers; matches already saved are skipped above.
                using var stats = await FaceitAsync(faceit, key, $"matches/{Uri.EscapeDataString(item.MatchId)}/stats", true, cancellationToken);
                if (stats is not null && ParseFaceitMatch(userId, faceitId, item, stats.RootElement) is { } match)
                    matches.Add(match);
            }
            return new(StatsStatus.Ok, matches);
        }
        catch (Exception error) when (Reason(service, error) is { } reason)
        {
            LogLookupFailed(logger, service + " matches", reason);
            return new(StatsStatus.Error, null, reason);
        }
    }

    public async Task<StatsFetch<MatchDetail>> GetMatchDetailAsync(StatsGame game, string matchId, CancellationToken cancellationToken)
    {
        string service = game == StatsGame.Cs2 ? "FACEIT" : StratzKey is null ? "OpenDota" : "STRATZ";
        try
        {
            if (game == StatsGame.Cs2)
            {
                if (FaceitKey is not { } faceitKey)
                    return new(StatsStatus.NoAccount, null, "Faceit__ApiKey isn't set on the server, so CS2 stats are off.");
                using var faceit = clients.CreateClient(FaceitClient);
                using var stats = await FaceitAsync(faceit, faceitKey, $"matches/{Uri.EscapeDataString(matchId)}/stats", true, cancellationToken);
                return stats is not null && ParseFaceitDetail(matchId, stats.RootElement) is { } match
                    ? new(StatsStatus.Ok, match) : new(StatsStatus.NoPublicData, null, $"FACEIT has no stats for match {matchId}.");
            }
            if (!long.TryParse(matchId, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
                return new(StatsStatus.NoAccount, null, "Not a Dota 2 match id.");
            var names = await HeroesAsync(cancellationToken);
            if (StratzKey is { } key)
            {
                // STRATZ answers with exactly the fields shown, so one small call per match.
                using var answer = await StratzAsync(key, StratzMatchQuery(id), cancellationToken);
                if (answer.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("match", out var stratz)
                    && ParseStratzMatch(stratz, names) is { } fromStratz)
                    return new(StatsStatus.Ok, fromStratz);
            }
            using var open = await OpenDotaAsync($"matches/{id.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
            return ParseOpenDotaMatch(open.RootElement, names) is { } fromOpenDota
                ? new(StatsStatus.Ok, fromOpenDota) : new(StatsStatus.NoPublicData, null, $"Match {matchId} has no players yet.");
        }
        catch (Exception error) when (Reason(service, error) is { } reason)
        {
            LogLookupFailed(logger, service + " match", reason);
            return new(StatsStatus.Error, null, reason);
        }
    }

    internal static string StratzMatchQuery(long matchId) =>
        $"{{ match(id: {matchId.ToString(CultureInfo.InvariantCulture)}) {{ id didRadiantWin gameMode lobbyType radiantKills direKills players {{ "
        + "steamAccountId isRadiant heroId kills deaths assists networth goldPerMinute experiencePerMinute numLastHits numDenies heroDamage "
        + "towerDamage heroHealing level position steamAccount { name isAnonymous } } } }";

    /// <summary>STRATZ's match: the score is the sum of kills per minute; private profiles keep their name hidden.</summary>
    internal static MatchDetail? ParseStratzMatch(JsonElement match, IReadOnlyDictionary<int, (string Name, string? Image)> heroes)
    {
        if (match.ValueKind != JsonValueKind.Object || !match.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array
            || players.GetArrayLength() == 0 || Number(match, "id") is not { } id)
            return null;
        static int Sum(JsonElement match, string name) =>
            match.TryGetProperty(name, out var minutes) && minutes.ValueKind == JsonValueKind.Array
                ? minutes.EnumerateArray().Sum(minute => minute.ValueKind == JsonValueKind.Number ? minute.GetInt32() : 0) : 0;
        int? winner = match.TryGetProperty("didRadiantWin", out var won) && won.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? won.ValueKind == JsonValueKind.True ? 0 : 1 : null;
        var list = players.EnumerateArray().Select(player =>
        {
            int hero = Int(player, "heroId") ?? 0;
            var known = heroes.TryGetValue(hero, out var found) ? found : ((string Name, string? Image)?)null;
            var account = player.TryGetProperty("steamAccount", out var steam) ? steam : default;
            bool hidden = account.ValueKind != JsonValueKind.Object || (account.TryGetProperty("isAnonymous", out var anonymous) && anonymous.ValueKind == JsonValueKind.True);
            int? position = Text(player, "position") is { } text && text.StartsWith("POSITION_", StringComparison.Ordinal)
                && int.TryParse(text[9..], NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
            return new MatchParticipant(player.TryGetProperty("isRadiant", out var radiant) && radiant.ValueKind == JsonValueKind.True ? 0 : 1,
                Number(player, "steamAccountId") is { } steamId ? ((long)steamId).ToString(CultureInfo.InvariantCulture) : null,
                hidden ? null : Text(account, "name"), known?.Name ?? (hero > 0 ? "#" + hero.ToString(CultureInfo.InvariantCulture) : null), known?.Image,
                Int(player, "kills") ?? 0, Int(player, "deaths") ?? 0, Int(player, "assists") ?? 0)
            {
                Level = Int(player, "level"), NetWorth = Int(player, "networth"), GoldPerMinute = Int(player, "goldPerMinute"),
                XpPerMinute = Int(player, "experiencePerMinute"), LastHits = Int(player, "numLastHits"), Denies = Int(player, "numDenies"),
                HeroDamage = Int(player, "heroDamage"), TowerDamage = Int(player, "towerDamage"), Healing = Int(player, "heroHealing"), Position = position
            };
        }).OrderBy(player => player.Side).ThenBy(player => player.Position ?? 9).ToArray();
        string? mode = Text(match, "gameMode") is { } gameMode ? gameMode.ToLowerInvariant() : null;
        bool ranked = Text(match, "lobbyType") == "RANKED" || mode == "all_pick_ranked";
        return new MatchDetail(StatsGame.Dota2, ((long)id).ToString(CultureInfo.InvariantCulture), winner,
            [new MatchSide(0, "Radiant", Sum(match, "radiantKills")), new MatchSide(1, "Dire", Sum(match, "direKills"))], list,
            mode == "all_pick_ranked" ? "all_pick" : mode, ranked);
    }

    /// <summary>OpenDota's game modes by number, as lower-case codes.</summary>
    private static readonly Dictionary<int, string> OpenDotaModes = new()
    {
        [1] = "all_pick", [2] = "captains_mode", [3] = "random_draft", [4] = "single_draft", [5] = "all_random", [16] = "captains_draft",
        [18] = "ability_draft", [22] = "all_pick", [23] = "turbo"
    };

    /// <summary>OpenDota's match: players without a public account id have no name.</summary>
    internal static MatchDetail? ParseOpenDotaMatch(JsonElement match, IReadOnlyDictionary<int, (string Name, string? Image)> heroes)
    {
        if (match.ValueKind != JsonValueKind.Object || !match.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array
            || players.GetArrayLength() == 0 || Number(match, "match_id") is not { } id)
            return null;
        int? winner = match.TryGetProperty("radiant_win", out var won) && won.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? won.ValueKind == JsonValueKind.True ? 0 : 1 : null;
        var list = players.EnumerateArray().Select(player =>
        {
            int hero = Int(player, "hero_id") ?? 0;
            var known = heroes.TryGetValue(hero, out var found) ? found : ((string Name, string? Image)?)null;
            long? account = Number(player, "account_id") is { } value ? (long)value : null;
            bool radiant = player.TryGetProperty("isRadiant", out var side) ? side.ValueKind == JsonValueKind.True : (Int(player, "player_slot") ?? 0) < 128;
            return new MatchParticipant(radiant ? 0 : 1, account?.ToString(CultureInfo.InvariantCulture), account is null ? null : Text(player, "personaname"),
                known?.Name ?? (hero > 0 ? "#" + hero.ToString(CultureInfo.InvariantCulture) : null), known?.Image,
                Int(player, "kills") ?? 0, Int(player, "deaths") ?? 0, Int(player, "assists") ?? 0)
            {
                Level = Int(player, "level"), NetWorth = Int(player, "net_worth"), GoldPerMinute = Int(player, "gold_per_min"), XpPerMinute = Int(player, "xp_per_min"),
                LastHits = Int(player, "last_hits"), Denies = Int(player, "denies"), HeroDamage = Int(player, "hero_damage"),
                TowerDamage = Int(player, "tower_damage"), Healing = Int(player, "hero_healing")
            };
        }).OrderBy(player => player.Side).ToArray();
        int? gameMode = Int(match, "game_mode");
        return new MatchDetail(StatsGame.Dota2, ((long)id).ToString(CultureInfo.InvariantCulture), winner,
            [new MatchSide(0, "Radiant", Int(match, "radiant_score")), new MatchSide(1, "Dire", Int(match, "dire_score"))], list,
            gameMode is { } number && OpenDotaModes.TryGetValue(number, out var code) ? code : null, Int(match, "lobby_type") == RankedLobby);
    }

    /// <summary>FACEIT's match stats: both teams with their final score and every player's numbers.</summary>
    internal static MatchDetail? ParseFaceitDetail(string matchId, JsonElement stats)
    {
        if (!stats.TryGetProperty("rounds", out var rounds) || rounds.ValueKind != JsonValueKind.Array || rounds.GetArrayLength() == 0)
            return null;
        var round = rounds[0];
        if (!round.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
            return null;
        var roundStats = round.TryGetProperty("round_stats", out var values) ? values : default;
        var sides = new List<MatchSide>();
        var list = new List<MatchParticipant>();
        int? winner = null;
        int side = 0;
        foreach (var team in teams.EnumerateArray())
        {
            var teamStats = team.TryGetProperty("team_stats", out var found) ? found : default;
            sides.Add(new MatchSide(side, Text(teamStats, "Team") ?? (side == 0 ? "Team 1" : "Team 2"), Int(teamStats, "Final Score")));
            if (Text(teamStats, "Team Win") == "1")
                winner = side;
            if (team.TryGetProperty("players", out var members) && members.ValueKind == JsonValueKind.Array)
                foreach (var member in members.EnumerateArray())
                {
                    var numbers = member.TryGetProperty("player_stats", out var playerStats) ? playerStats : default;
                    list.Add(new MatchParticipant(side, Text(member, "player_id"), Text(member, "nickname"), null, null,
                        Int(numbers, "Kills") ?? 0, Int(numbers, "Deaths") ?? 0, Int(numbers, "Assists") ?? 0)
                    {
                        Headshots = Number(numbers, "Headshots %"), Adr = Number(numbers, "ADR"), Mvps = Int(numbers, "MVPs")
                    });
                }
            side++;
        }
        return list.Count == 0 ? null : new MatchDetail(StatsGame.Cs2, matchId, winner, sides,
            list.OrderBy(player => player.Side).ThenByDescending(player => player.Kills).ToArray(),
            Map: roundStats.ValueKind == JsonValueKind.Object ? Text(roundStats, "Map") : null);
    }

    public async Task<StatsFetch<DotaMeta>> GetMetaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var names = await HeroesAsync(cancellationToken);
            var lanes = new Dictionary<DotaLane, IReadOnlyList<MetaHero>>();
            foreach (var lane in Enum.GetValues<DotaLane>())
            {
                using var answer = await OpenDotaAsync("scenarios/laneRoles", $"lane_role={(int)lane}", cancellationToken);
                lanes[lane] = ParseLaneRoles(answer.RootElement, names);
            }
            return new(StatsStatus.Ok, new DotaMeta(DateTimeOffset.UtcNow, lanes));
        }
        catch (Exception error) when (Reason("OpenDota", error) is { } reason)
        {
            LogLookupFailed(logger, "OpenDota meta", reason);
            return new(StatsStatus.Error, null, reason);
        }
    }

    /// <summary>
    /// OpenDota's lane-role scenarios come in game-length buckets; they are summed per hero. Heroes with at least
    /// 0.5% of the lane's games (and 100 games) are ranked by win rate, so rare picks with lucky numbers stay out.
    /// </summary>
    internal static IReadOnlyList<MetaHero> ParseLaneRoles(JsonElement answer, IReadOnlyDictionary<int, (string Name, string? Image)> names)
    {
        if (answer.ValueKind != JsonValueKind.Array)
            return [];
        var totals = answer.EnumerateArray()
            .Select(row => (Hero: Int(row, "hero_id") ?? 0, Games: Int(row, "games") ?? 0, Wins: Int(row, "wins") ?? 0))
            .Where(row => row.Hero > 0)
            .GroupBy(row => row.Hero)
            .Select(hero => (Hero: hero.Key, Games: hero.Sum(row => row.Games), Wins: hero.Sum(row => row.Wins)))
            .ToArray();
        int all = totals.Sum(hero => hero.Games);
        int minimum = Math.Max(100, all / 200);
        return totals.Where(hero => hero.Games >= minimum)
            .OrderByDescending(hero => (double)hero.Wins / hero.Games).ThenByDescending(hero => hero.Games)
            .Take(HappyMix.Application.Stats.DotaMetas.MaxPerLane)
            .Select(hero => names.TryGetValue(hero.Hero, out var known)
                ? new MetaHero(hero.Hero, known.Name, known.Image, hero.Games, hero.Wins)
                : new MetaHero(hero.Hero, "#" + hero.Hero.ToString(CultureInfo.InvariantCulture), null, hero.Games, hero.Wins))
            .ToArray();
    }

    public async Task<StatsFetch<HeroMeta>> GetHeroMetaAsync(MetaFilter filter, CancellationToken cancellationToken)
    {
        if (StratzKey is not { } key)
            return new(StatsStatus.NoAccount, null, "Stratz__ApiKey isn't set on the server.");
        try
        {
            var names = await HeroesAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var current = filter.Patch == MetaPatch.Current ? await CurrentPatchAsync(cancellationToken) : null;
            // Days are exact for a week; an older patch is counted in STRATZ's weeks.
            bool daily = current is not { } released || now - released.Date <= TimeSpan.FromDays(7);
            DateTimeOffset from = current is { } since ? StartOf(since.Date, daily) : now.AddDays(-7);

            using var document = await StratzAsync(key, StratzQuery(filter, daily), cancellationToken);
            var rows = document.RootElement.GetProperty("data").GetProperty("heroStats").GetProperty(daily ? "winDay" : "winWeek");
            return new(StatsStatus.Ok, ParseStratz(filter, rows, daily ? "day" : "week", from, names, current?.Name, current?.Date, now));
        }
        catch (Exception error) when (Reason("STRATZ", error) is { } reason)
        {
            LogLookupFailed(logger, "STRATZ meta", reason);
            return new(StatsStatus.Error, null, reason);
        }
    }

    /// <summary>Posts a GraphQL query to STRATZ; a failed answer or a GraphQL error becomes a <see cref="LookupFailedException"/>.</summary>
    private async Task<JsonDocument> StratzAsync(string key, string query, CancellationToken cancellationToken)
    {
        string body = await SharedAsync("STRATZ " + query, async () =>
        {
            using var client = clients.CreateClient(StratzClient);
            using var request = new HttpRequestMessage(HttpMethod.Post, Stratz)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { query }), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new("Bearer", key);
            using var answer = await client.SendAsync(request, CancellationToken.None);
            string text = await answer.Content.ReadAsStringAsync(CancellationToken.None);
            if (!answer.IsSuccessStatusCode)
                throw new LookupFailedException($"STRATZ answered {(int)answer.StatusCode} {answer.ReasonPhrase}: {Snippet(text)}"
                    + (answer.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? " Check Stratz__ApiKey." : ""));
            return text;
        }, cancellationToken);
        var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            string message = Text(errors[0], "message") ?? Snippet(errors.ToString());
            document.Dispose();
            throw new LookupFailedException("STRATZ refused the query: " + message);
        }
        return document;
    }

    /// <summary>How many of a player's last matches STRATZ is asked about for their positions.</summary>
    internal const int PositionMatches = 50;

    internal static string StratzPositionsQuery(long accountId)
    {
        string id = accountId.ToString(CultureInfo.InvariantCulture);
        return $"{{ player(steamAccountId: {id}) {{ matches(request: {{ take: {PositionMatches}, lobbyTypeIds: [{RankedLobby}] }}) {{ players(steamAccountId: {id}) {{ position isVictory }} }} }} }}";
    }

    /// <summary>Games and wins per position (1–5) from STRATZ's last matches, most played first. Matches without a position are skipped.</summary>
    internal static IReadOnlyList<DotaRole> ParseStratzPositions(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("player", out var player) || player.ValueKind != JsonValueKind.Object
            || !player.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return [];
        var counts = new Dictionary<string, (int Games, int Wins)>(StringComparer.Ordinal);
        foreach (var match in matches.EnumerateArray())
        {
            if (!match.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array || players.GetArrayLength() == 0)
                continue;
            var line = players[0];
            string number = Text(line, "position")?.Replace("POSITION_", "", StringComparison.Ordinal) ?? "";
            if (number is not ("1" or "2" or "3" or "4" or "5"))
                continue;
            var count = counts.GetValueOrDefault(number);
            bool won = line.TryGetProperty("isVictory", out var victory) && victory.ValueKind == JsonValueKind.True;
            counts[number] = (count.Games + 1, count.Wins + (won ? 1 : 0));
        }
        return Roles(counts);
    }

    private static DotaRole[] Roles(Dictionary<string, (int Games, int Wins)> counts) =>
        counts.Select(item => new DotaRole(item.Key, item.Value.Games, item.Value.Wins))
            .OrderByDescending(role => role.Games).ThenByDescending(role => role.Wins).ThenBy(role => role.Key, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The last ranked matches from OpenDota's player matches: form, streak, averages, solo vs party, ranked results, and
    /// games per lane (OpenDota's lane_role; is_roaming counts as "roam"). Null when there are no matches.
    /// </summary>
    internal static (DotaRecent? Recent, IReadOnlyList<DotaRole> Lanes) ParseRecent(JsonElement recent)
    {
        if (recent.ValueKind != JsonValueKind.Array)
            return (null, []);
        var form = new System.Text.StringBuilder();
        // Averages leave out Turbo (game mode 23), where gold and experience run far higher, unless every match is Turbo.
        int normal = 0, kills = 0, deaths = 0, assists = 0, gpm = 0, xpm = 0;
        int turbo = 0, turboKills = 0, turboDeaths = 0, turboAssists = 0, turboGpm = 0, turboXpm = 0;
        int wins = 0, solo = 0, soloWins = 0, party = 0, partyWins = 0, ranked = 0, rankedWins = 0;
        var lanes = new Dictionary<string, (int Games, int Wins)>(StringComparer.Ordinal);
        // OpenDota lists the newest match first.
        foreach (var match in recent.EnumerateArray().Where(match => Number(match, "match_id") is not null))
        {
            bool radiant = (Int(match, "player_slot") ?? 0) < 128;
            bool won = radiant == (match.TryGetProperty("radiant_win", out var radiantWin) && radiantWin.ValueKind == JsonValueKind.True);
            form.Append(won ? 'W' : 'L');
            wins += won ? 1 : 0;
            if (Int(match, "game_mode") == 23)
            {
                turbo++;
                turboKills += Int(match, "kills") ?? 0;
                turboDeaths += Int(match, "deaths") ?? 0;
                turboAssists += Int(match, "assists") ?? 0;
                turboGpm += Int(match, "gold_per_min") ?? 0;
                turboXpm += Int(match, "xp_per_min") ?? 0;
            }
            else
            {
                normal++;
                kills += Int(match, "kills") ?? 0;
                deaths += Int(match, "deaths") ?? 0;
                assists += Int(match, "assists") ?? 0;
                gpm += Int(match, "gold_per_min") ?? 0;
                xpm += Int(match, "xp_per_min") ?? 0;
            }
            if ((Int(match, "party_size") ?? 1) <= 1) { solo++; soloWins += won ? 1 : 0; }
            else { party++; partyWins += won ? 1 : 0; }
            // Ranked matchmaking is lobby type 7; those are the games that move MMR.
            if (Int(match, "lobby_type") == 7) { ranked++; rankedWins += won ? 1 : 0; }
            string? lane = match.TryGetProperty("is_roaming", out var roaming) && roaming.ValueKind == JsonValueKind.True ? "roam"
                : Int(match, "lane_role") switch { 1 => "safe", 2 => "mid", 3 => "off", 4 => "jungle", _ => null };
            if (lane is not null)
            {
                var count = lanes.GetValueOrDefault(lane);
                lanes[lane] = (count.Games + 1, count.Wins + (won ? 1 : 0));
            }
        }
        int games = form.Length;
        if (games == 0)
            return (null, []);
        if (normal == 0)
            (normal, kills, deaths, assists, gpm, xpm) = (turbo, turboKills, turboDeaths, turboAssists, turboGpm, turboXpm);
        string text = form.ToString();
        int streak = text.TakeWhile(result => result == text[0]).Count() * (text[0] == 'W' ? 1 : -1);
        return (new DotaRecent(games, wins, text, streak, Math.Round(kills / (double)normal, 1), Math.Round(deaths / (double)normal, 1),
            Math.Round(assists / (double)normal, 1), gpm / normal, xpm / normal, solo, soloWins, party, partyWins, ranked, rankedWins), Roles(lanes));
    }

    /// <summary>How many teammates from OpenDota's peers are kept on the card (enough to find the group's members among them).</summary>
    internal const int PeersKept = 100;

    /// <summary>OpenDota's peers: people played with on the same team, most games first.</summary>
    internal static IReadOnlyList<DotaPeer> ParsePeers(JsonElement peers) =>
        peers.ValueKind != JsonValueKind.Array ? [] : peers.EnumerateArray()
            .Select(peer => new DotaPeer((long)(Number(peer, "account_id") ?? 0), Int(peer, "with_games") ?? 0, Int(peer, "with_win") ?? 0))
            .Where(peer => peer.AccountId > 0 && peer.Games > 0)
            .OrderByDescending(peer => peer.Games).ThenBy(peer => peer.AccountId)
            .Take(PeersKept).ToArray();

    /// <summary>
    /// The GraphQL query for one filter: ranked All Pick only, per hero, by day (or week), for the role and ranks.
    /// </summary>
    internal static string StratzQuery(MetaFilter filter, bool daily)
    {
        var arguments = new List<string> { "gameModeIds: [ALL_PICK_RANKED]", "groupBy: HERO_ID" };
        if (filter.Position is >= 1 and <= 5)
            arguments.Add($"positionIds: [POSITION_{filter.Position.ToString(CultureInfo.InvariantCulture)}]");
        if (Brackets(filter.Rank) is { Length: > 0 } brackets)
            arguments.Add($"bracketIds: [{string.Join(", ", brackets)}]");
        string field = daily ? "winDay" : "winWeek", stamp = daily ? "day" : "week";
        return $"{{ heroStats {{ {field}({string.Join(", ", arguments)}) {{ {stamp} heroId winCount matchCount }} }} }}";
    }

    internal static string[] Brackets(MetaRank rank) => rank switch
    {
        MetaRank.HeraldGuardian => ["HERALD", "GUARDIAN"],
        MetaRank.CrusaderArchon => ["CRUSADER", "ARCHON"],
        MetaRank.LegendAncient => ["LEGEND", "ANCIENT"],
        MetaRank.DivineImmortal => ["DIVINE", "IMMORTAL"],
        _ => []
    };

    /// <summary>
    /// The first day (or week) to count after a patch released at <paramref name="released"/>: a patch out after midday
    /// UTC starts with the next day, so a day mostly played on the old patch isn't mixed in.
    /// </summary>
    internal static DateTimeOffset StartOf(DateTimeOffset released, bool daily)
    {
        var day = new DateTimeOffset(released.UtcDateTime.Date, TimeSpan.Zero);
        return daily ? (released.UtcDateTime.Hour >= 12 ? day.AddDays(1) : day) : released;
    }

    /// <summary>
    /// Sums STRATZ day (or week) rows from <paramref name="from"/> per hero. Heroes with at least 0.25% of the role's
    /// games (and 50 games) count. Pick rate is per match: a role has 2 heroes a match, any role 10.
    /// </summary>
    internal static HeroMeta ParseStratz(MetaFilter filter, JsonElement rows, string stamp, DateTimeOffset from,
        IReadOnlyDictionary<int, (string Name, string? Image)> names, string? patchName, DateTimeOffset? released, DateTimeOffset now)
    {
        long fromSeconds = from.ToUnixTimeSeconds();
        var totals = rows.ValueKind != JsonValueKind.Array ? [] : rows.EnumerateArray()
            .Where(row => Number(row, stamp) is { } at && (long)at >= fromSeconds - (stamp == "week" ? 7 * 86400 - 1 : 0))
            .Select(row => (Hero: Int(row, "heroId") ?? 0, Games: (long)(Number(row, "matchCount") ?? 0), Wins: (long)(Number(row, "winCount") ?? 0)))
            .Where(row => row.Hero > 0)
            .GroupBy(row => row.Hero)
            .Select(hero => (Hero: hero.Key, Games: hero.Sum(row => row.Games), Wins: hero.Sum(row => row.Wins)))
            .ToArray();
        long all = totals.Sum(hero => hero.Games);
        int perMatch = filter.Position is >= 1 and <= 5 ? 2 : 10;
        long minimum = Math.Max(50, all / 400);
        var heroes = totals.Where(hero => hero.Games >= minimum)
            .Select(hero => names.TryGetValue(hero.Hero, out var known)
                ? new RankedHero(hero.Hero, known.Name, known.Image, (int)Math.Min(int.MaxValue, hero.Games), (int)Math.Min(int.MaxValue, hero.Wins),
                    all == 0 ? 0 : hero.Games * 100.0 * perMatch / all)
                : new RankedHero(hero.Hero, "#" + hero.Hero.ToString(CultureInfo.InvariantCulture), null, (int)Math.Min(int.MaxValue, hero.Games),
                    (int)Math.Min(int.MaxValue, hero.Wins), all == 0 ? 0 : hero.Games * 100.0 * perMatch / all))
            .OrderByDescending(hero => hero.Score)
            .ToArray();
        return new HeroMeta(filter, patchName, filter.Patch == MetaPatch.Current ? released : from, now, all / perMatch, heroes);
    }

    /// <summary>The newest Dota 2 patch and when it came out, from OpenDota's patch list (STRATZ's list lags behind).</summary>
    private async Task<(string Name, DateTimeOffset Date)?> CurrentPatchAsync(CancellationToken cancellationToken)
    {
        if (patch is { } known && DateTimeOffset.UtcNow - patchAt < PatchFor)
            return known;
        using var document = await OpenDotaAsync("constants/patch", null, cancellationToken);
        patch = LatestPatch(document.RootElement) ?? patch;
        patchAt = DateTimeOffset.UtcNow;
        return patch;
    }

    internal static (string Name, DateTimeOffset Date)? LatestPatch(JsonElement patches) =>
        patches.ValueKind != JsonValueKind.Array ? null : patches.EnumerateArray()
            .Select(item => (Name: Text(item, "name"), Date: Text(item, "date")))
            .Where(item => item.Name is not null && DateTimeOffset.TryParse(item.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            .Select(item => (item.Name!, DateTimeOffset.Parse(item.Date!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))
            .OrderBy(item => item.Item2)
            .Select(item => ((string Name, DateTimeOffset Date)?)item)
            .LastOrDefault();

    private static string Snippet(string body) => (body.Length > 160 ? body[..160] + "…" : body).ReplaceLineEndings(" ");

    /// <summary>Why a lookup failed, in one line for the logs and the card; null for exceptions that aren't lookup problems.</summary>
    internal static string? Reason(string service, Exception error) => error switch
    {
        LookupFailedException failed => failed.Message,
        TaskCanceledException => $"{service} didn't answer within {(service == "OpenDota" ? OpenDotaSeconds : 10).ToString(CultureInfo.InvariantCulture)} seconds.",
        HttpRequestException http => $"{service} couldn't be reached: {http.Message}",
        JsonException => $"{service} sent an answer that isn't valid JSON.",
        _ => null
    };

    /// <summary>A failed answer: the service, the endpoint (never the key), the HTTP status and the start of the body.</summary>
    internal sealed class LookupFailedException(string message) : Exception(message);

    /// <summary>
    /// One OpenDota GET. While the same URL is already on its way (a Refresh click during the hourly refresh, two
    /// people opening the same page), the caller waits for that answer instead of sending the request again.
    /// </summary>
    private async Task<JsonDocument> OpenDotaAsync(string path, string? query, CancellationToken cancellationToken)
    {
        string url = OpenDotaUrl(path, query);
        string body = await SharedAsync(url, async () =>
        {
            OpenDotaCallsToday(1);
            using var client = clients.CreateClient(OpenDotaClient);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var answer = await client.SendAsync(request, CancellationToken.None);
            string text = await answer.Content.ReadAsStringAsync(CancellationToken.None);
            if (!answer.IsSuccessStatusCode)
                throw Failed("OpenDota", path, answer, text);
            return text;
        }, cancellationToken);
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Runs <paramref name="send"/> once for all callers with the same <paramref name="key"/> while it is on its way.
    /// The call itself isn't cancelled when one caller gives up (the client's timeout ends it), so the others still get it.
    /// </summary>
    internal async Task<string> SharedAsync(string key, Func<Task<string>> send, CancellationToken cancellationToken)
    {
        var shared = inFlight.GetOrAdd(key, _ =>
        {
            // Each candidate removes only itself, so a losing candidate of a race never removes the stored one.
            Lazy<Task<string>>? self = null;
            self = new Lazy<Task<string>>(async () =>
            {
                try
                {
                    return await send();
                }
                finally
                {
                    inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(key, self!));
                }
            });
            return self;
        });
        return await shared.Value.WaitAsync(cancellationToken);
    }

    private static LookupFailedException Failed(string service, string path, HttpResponseMessage answer, string body)
    {
        string hint = answer.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when service == "FACEIT" => " Check Faceit__ApiKey: it must be a server-side key.",
            HttpStatusCode.TooManyRequests => " The service's rate limit was hit; the next refresh tries again.",
            _ => ""
        };
        string snippet = body.Length > 160 ? body[..160] + "…" : body;
        return new LookupFailedException(
            $"{service} answered {(int)answer.StatusCode} {answer.ReasonPhrase} for {path}: {snippet.ReplaceLineEndings(" ")}{hint}");
    }

    private static Task<JsonDocument?> FaceitAsync(HttpClient client, string key, string path, bool allowNotFound, CancellationToken cancellationToken) =>
        RequestAsync(client, "FACEIT", path.Split('?')[0], Faceit + path, key, allowNotFound, cancellationToken);

    private static async Task<JsonDocument?> RequestAsync(HttpClient client, string service, string path, string url, string? bearer, bool allowNotFound,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null)
            request.Headers.Authorization = new("Bearer", bearer);
        using var answer = await client.SendAsync(request, cancellationToken);
        if (allowNotFound && answer.StatusCode == HttpStatusCode.NotFound)
            return null;
        string body = await answer.Content.ReadAsStringAsync(cancellationToken);
        if (!answer.IsSuccessStatusCode)
            throw Failed(service, path, answer, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>OpenDota's recentMatches: the side is Radiant when player_slot is below 128.</summary>
    internal static IReadOnlyList<PlayerMatch> ParseDotaMatches(ulong userId, JsonElement recent, IReadOnlyDictionary<int, (string Name, string? Image)> heroes) =>
        recent.ValueKind != JsonValueKind.Array ? [] : recent.EnumerateArray()
            .Where(match => Number(match, "match_id") is not null && Number(match, "start_time") is not null)
            .Select(match =>
            {
                int hero = Int(match, "hero_id") ?? 0;
                bool radiant = (Int(match, "player_slot") ?? 0) < 128;
                bool radiantWon = match.TryGetProperty("radiant_win", out var won) && won.ValueKind == JsonValueKind.True;
                var known = heroes.TryGetValue(hero, out var found) ? found : ((string Name, string? Image)?)null;
                return new PlayerMatch(userId, StatsGame.Dota2, ((long)Number(match, "match_id")!.Value).ToString(CultureInfo.InvariantCulture),
                    DateTimeOffset.FromUnixTimeSeconds((long)Number(match, "start_time")!.Value), Int(match, "duration") ?? 0, radiant == radiantWon,
                    known?.Name ?? (hero > 0 ? "#" + hero.ToString(CultureInfo.InvariantCulture) : null), known?.Image,
                    Int(match, "kills") ?? 0, Int(match, "deaths") ?? 0, Int(match, "assists") ?? 0);
            })
            .ToArray();

    internal sealed record FaceitItem(string MatchId, DateTimeOffset StartedAt, int DurationSeconds, string? Winner, IReadOnlyDictionary<string, string> TeamOf);

    /// <summary>FACEIT's match history: ids, times, the winning faction and which faction each player was on.</summary>
    internal static IReadOnlyList<FaceitItem> FaceitHistory(JsonElement history)
    {
        if (!history.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<FaceitItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "match_id") is not { } id || Number(item, "started_at") is not { } started)
                continue;
            var teamOf = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("teams", out var teams) && teams.ValueKind == JsonValueKind.Object)
                foreach (var team in teams.EnumerateObject())
                    if (team.Value.TryGetProperty("players", out var members) && members.ValueKind == JsonValueKind.Array)
                        foreach (var member in members.EnumerateArray())
                            if (Text(member, "player_id") is { } playerId)
                                teamOf[playerId] = team.Name;
            string? winner = item.TryGetProperty("results", out var results) ? Text(results, "winner") : null;
            int duration = Number(item, "finished_at") is { } finished ? (int)Math.Max(0, finished - started) : 0;
            result.Add(new FaceitItem(id, DateTimeOffset.FromUnixTimeSeconds((long)started), duration, winner, teamOf));
        }
        return result;
    }

    /// <summary>One player's line from FACEIT's match stats: map, score from their side, kills, deaths, assists, headshots.</summary>
    internal static PlayerMatch? ParseFaceitMatch(ulong userId, string faceitId, FaceitItem item, JsonElement stats)
    {
        if (!stats.TryGetProperty("rounds", out var rounds) || rounds.ValueKind != JsonValueKind.Array || rounds.GetArrayLength() == 0)
            return null;
        var round = rounds[0];
        var roundStats = round.TryGetProperty("round_stats", out var values) ? values : default;
        if (!round.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var team in teams.EnumerateArray())
        {
            if (!team.TryGetProperty("players", out var members) || members.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var member in members.EnumerateArray())
            {
                if (Text(member, "player_id") != faceitId || !member.TryGetProperty("player_stats", out var numbers))
                    continue;
                bool won = team.TryGetProperty("team_stats", out var teamStats) && Text(teamStats, "Team Win") is { } win ? win == "1"
                    : item.Winner is { } winner && item.TeamOf.TryGetValue(faceitId, out var side) && side == winner;
                string? score = roundStats.ValueKind == JsonValueKind.Object ? Text(roundStats, "Score") : null;
                return new PlayerMatch(userId, StatsGame.Cs2, item.MatchId, item.StartedAt, item.DurationSeconds, won,
                    roundStats.ValueKind == JsonValueKind.Object ? Text(roundStats, "Map") : null, null,
                    Int(numbers, "Kills") ?? 0, Int(numbers, "Deaths") ?? 0, Int(numbers, "Assists") ?? 0, Number(numbers, "Headshots %"), score);
            }
        }
        return null;
    }

    /// <summary>
    /// A Dota card from OpenDota's player, win/loss and heroes answers. Null when the account has no public data:
    /// OpenDota then knows no rank and no matches.
    /// </summary>
    internal static DotaProfile? ParseDota(long accountId, JsonElement player, JsonElement record, JsonElement played,
        IReadOnlyDictionary<int, (string Name, string? Image)> heroes)
    {
        int? rank = Int(player, "rank_tier");
        int wins = Int(record, "win") ?? 0, losses = Int(record, "lose") ?? 0;
        var top = played.ValueKind != JsonValueKind.Array ? [] : played.EnumerateArray()
            .Select(hero => (Id: Int(hero, "hero_id") ?? 0, Games: Int(hero, "games") ?? 0, Wins: Int(hero, "win") ?? 0))
            .Where(hero => hero.Id > 0 && hero.Games > 0)
            .OrderByDescending(hero => hero.Games).ThenByDescending(hero => hero.Wins).Take(3)
            .Select(hero => heroes.TryGetValue(hero.Id, out var known)
                ? new DotaHero(hero.Id, known.Name, known.Image, hero.Games, hero.Wins)
                : new DotaHero(hero.Id, "#" + hero.Id.ToString(CultureInfo.InvariantCulture), null, hero.Games, hero.Wins))
            .ToArray();
        if (rank is null && wins + losses == 0 && top.Length == 0)
            return null;
        string? persona = player.TryGetProperty("profile", out var profile) ? Text(profile, "personaname") : null;
        return new DotaProfile(accountId, persona, rank, Int(player, "leaderboard_rank"), wins, losses, top);
    }

    /// <summary>OpenDota's hero list: names and pictures on Steam's CDN.</summary>
    internal static IReadOnlyDictionary<int, (string Name, string? Image)> ParseHeroes(JsonElement constants)
    {
        var result = new Dictionary<int, (string Name, string? Image)>();
        if (constants.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var hero in constants.EnumerateObject())
            if (Int(hero.Value, "id") is { } id && Text(hero.Value, "localized_name") is { } name)
                result[id] = (name, Text(hero.Value, "img") is { Length: > 0 } image && image.StartsWith('/') ? SteamCdn + image.TrimEnd('?') : null);
        return result;
    }

    /// <summary>A CS2 card from FACEIT's player lookup and (when it exists) the player's CS2 stats.</summary>
    internal static Cs2Profile ParseCs2(JsonElement player, JsonElement? stats)
    {
        JsonElement cs2 = default;
        bool hasGame = player.TryGetProperty("games", out var games) && games.TryGetProperty("cs2", out cs2);
        var lifetime = stats is { } root && root.TryGetProperty("lifetime", out var life) ? life : default;
        var maps = stats is { } all && all.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array
            ? segments.EnumerateArray()
                .Where(segment => Text(segment, "type") == "Map" && Text(segment, "label") is { Length: > 0 })
                .Select(segment => segment.TryGetProperty("stats", out var values)
                    ? new Cs2Map(Text(segment, "label")!, Number(values, "Matches") is { } matches ? (int)matches : 0, Number(values, "Win Rate %"))
                    : null)
                .OfType<Cs2Map>().Where(map => map.Matches > 0).OrderByDescending(map => map.Matches).Take(2).ToArray()
            : [];
        return new Cs2Profile(Text(player, "player_id") ?? "", Text(player, "nickname") ?? "",
            hasGame ? Int(cs2, "skill_level") : null, hasGame ? Int(cs2, "faceit_elo") : null,
            lifetime.ValueKind == JsonValueKind.Object && Number(lifetime, "Matches") is { } total ? (int)total : 0,
            lifetime.ValueKind == JsonValueKind.Object ? Number(lifetime, "Win Rate %") : null,
            lifetime.ValueKind == JsonValueKind.Object ? Number(lifetime, "Average K/D Ratio") : null,
            lifetime.ValueKind == JsonValueKind.Object ? Number(lifetime, "Average Headshots %") : null,
            maps);
    }

    private async Task<IReadOnlyDictionary<int, (string Name, string? Image)>> HeroesAsync(CancellationToken cancellationToken)
    {
        if (heroes is { } known && DateTimeOffset.UtcNow - heroesAt < HeroesFor)
            return known;
        await heroesLock.WaitAsync(cancellationToken);
        try
        {
            if (heroes is { } again && DateTimeOffset.UtcNow - heroesAt < HeroesFor)
                return again;
            using var document = await OpenDotaAsync("constants/heroes", null, cancellationToken);
            heroes = ParseHeroes(document.RootElement);
            heroesAt = DateTimeOffset.UtcNow;
            return heroes;
        }
        finally
        {
            heroesLock.Release();
        }
    }

    private string OpenDotaUrl(string path, string? query = null)
    {
        var parts = new List<string>();
        if (query is not null) parts.Add(query);
        if (OpenDotaKey is { } key) parts.Add("api_key=" + Uri.EscapeDataString(key));
        return OpenDota + path + (parts.Count > 0 ? "?" + string.Join('&', parts) : "");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>Numbers that may come as JSON numbers or as strings (FACEIT sends "52" and "1.12").</summary>
    private static double? Number(JsonElement element, string name) =>
        element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed
        : null;

    private static int? Int(JsonElement element, string name) => Number(element, name) is { } value ? (int)Math.Round(value) : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} stats lookup failed: {Reason}")]
    private static partial void LogLookupFailed(ILogger logger, string service, string reason);
}
