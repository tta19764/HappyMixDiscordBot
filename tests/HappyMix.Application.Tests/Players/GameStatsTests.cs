using System.Text.Json;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Guilds;
using HappyMix.Application.Players;
using HappyMix.Application.Stats;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Infrastructure.Stats;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Players;

/// <summary>Dota 2 and CS2 cards: parsing OpenDota and FACEIT answers, opt-in privacy, refresh and errors.</summary>
public sealed class GameStatsTests
{
    private const ulong Guild = 1, Kai = 10, Mira = 20, Taro = 30;
    private const string KaiSteam = "76561198000000001", MiraSteam = "76561198000000002";

    [Fact]
    public void OpenDotaAnswersBecomeACard()
    {
        using var player = JsonDocument.Parse("""
            {"profile":{"account_id":111458067,"personaname":"Tyler"},"rank_tier":34,"leaderboard_rank":null}
            """);
        using var record = JsonDocument.Parse("""{"win":14,"lose":9}""");
        using var heroes = JsonDocument.Parse("""
            [{"hero_id":14,"games":22,"win":13},{"hero_id":"25","games":15,"win":7},{"hero_id":1,"games":0,"win":0},{"hero_id":26,"games":11,"win":6},{"hero_id":2,"games":3,"win":1}]
            """);
        using var constants = JsonDocument.Parse("""
            {"14":{"id":14,"localized_name":"Pudge","img":"/apps/dota2/images/dota_react/heroes/pudge.png?"},
             "25":{"id":25,"localized_name":"Lina","img":"/apps/dota2/images/dota_react/heroes/lina.png?"}}
            """);
        var names = GameStatsDirectory.ParseHeroes(constants.RootElement);
        Assert.Equal(("Pudge", "https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/pudge.png"), names[14]);

        var card = GameStatsDirectory.ParseDota(111458067, player.RootElement, record.RootElement, heroes.RootElement, names)!;
        Assert.Equal(("Tyler", 34, 14, 9), (card.Persona, card.RankTier, card.Wins, card.Losses));
        Assert.Equal(["Pudge", "Lina", "#26"], card.TopHeroes.Select(hero => hero.Name));
        Assert.Equal((3, 4), GameStats.DotaMedal(card.RankTier));

        // No rank, no matches, no heroes: the account doesn't expose match data.
        using var hidden = JsonDocument.Parse("""{"profile":{"account_id":5},"rank_tier":null}""");
        using var none = JsonDocument.Parse("""{"win":0,"lose":0}""");
        using var empty = JsonDocument.Parse("[]");
        Assert.Null(GameStatsDirectory.ParseDota(5, hidden.RootElement, none.RootElement, empty.RootElement, names));
    }

    [Fact]
    public void RecentMatchesPeersAndPositionsAddToTheDotaCard()
    {
        // Newest first: two wins, then a loss and a win. Ranked is lobby type 7; party_size above 1 is a party.
        using var recent = JsonDocument.Parse("""
            [{"match_id":4,"player_slot":1,"radiant_win":true,"kills":10,"deaths":2,"assists":5,"gold_per_min":600,"xp_per_min":700,"party_size":1,"lobby_type":7,"lane_role":2,"is_roaming":false},
             {"match_id":3,"player_slot":130,"radiant_win":false,"kills":6,"deaths":4,"assists":9,"gold_per_min":500,"xp_per_min":600,"party_size":2,"lobby_type":7,"lane_role":2,"is_roaming":false},
             {"match_id":2,"player_slot":2,"radiant_win":false,"kills":2,"deaths":8,"assists":4,"gold_per_min":300,"xp_per_min":400,"party_size":2,"lobby_type":0,"lane_role":3,"is_roaming":false},
             {"match_id":1,"player_slot":3,"radiant_win":true,"kills":4,"deaths":6,"assists":2,"gold_per_min":400,"xp_per_min":500,"party_size":null,"lobby_type":7,"lane_role":1,"is_roaming":true}]
            """);
        var (last, lanes) = GameStatsDirectory.ParseRecent(recent.RootElement);
        Assert.Equal(new DotaRecent(4, 3, "WWLW", 2, 5.5, 5, 5, 450, 550, 2, 2, 2, 1, 3, 3), last);
        Assert.Equal([new DotaRole("mid", 2, 2), new DotaRole("roam", 1, 1), new DotaRole("off", 1, 0)], lanes);
        Assert.Equal(75, GameStats.MmrEstimate(last)); // 3 ranked games, all won: (3 - 0) × 25
        using var empty = JsonDocument.Parse("[]");
        Assert.Null(GameStatsDirectory.ParseRecent(empty.RootElement).Recent);
        // Turbo games count for form and record but not for the averages, unless all games are Turbo.
        using var mixed = JsonDocument.Parse("""
            [{"match_id":2,"player_slot":1,"radiant_win":true,"kills":20,"deaths":2,"assists":30,"gold_per_min":1500,"xp_per_min":2000,"game_mode":23},
             {"match_id":1,"player_slot":1,"radiant_win":false,"kills":4,"deaths":6,"assists":8,"gold_per_min":400,"xp_per_min":500,"game_mode":22}]
            """);
        Assert.Equal((2, 1, 4.0, 400), GameStatsDirectory.ParseRecent(mixed.RootElement).Recent is { } both ? (both.Matches, both.Wins, both.Kills, both.Gpm) : default);
        using var turbo = JsonDocument.Parse("""[{"match_id":2,"player_slot":1,"radiant_win":true,"kills":20,"deaths":2,"assists":30,"gold_per_min":1500,"xp_per_min":2000,"game_mode":23}]""");
        Assert.Equal(1500, GameStatsDirectory.ParseRecent(turbo.RootElement).Recent!.Gpm);

        using var peers = JsonDocument.Parse("""
            [{"account_id":7,"with_games":3,"with_win":2,"games":9},{"account_id":8,"with_games":0,"with_win":0},{"account_id":9,"with_games":12,"with_win":5}]
            """);
        Assert.Equal([new DotaPeer(9, 12, 5), new DotaPeer(7, 3, 2)], GameStatsDirectory.ParsePeers(peers.RootElement));

        Assert.Equal("{ player(steamAccountId: 42) { matches(request: { take: 50, lobbyTypeIds: [7] }) { players(steamAccountId: 42) { position isVictory } } } }",
            GameStatsDirectory.StratzPositionsQuery(42));
        using var positions = JsonDocument.Parse("""
            {"data":{"player":{"matches":[
              {"players":[{"position":"POSITION_4","isVictory":true}]},{"players":[{"position":"POSITION_4","isVictory":false}]},
              {"players":[{"position":"POSITION_1","isVictory":true}]},{"players":[{"position":null,"isVictory":true}]},{"players":[]}]}}}
            """);
        Assert.Equal([new DotaRole("4", 2, 1), new DotaRole("1", 1, 1)], GameStatsDirectory.ParseStratzPositions(positions.RootElement));
        using var nobody = JsonDocument.Parse("""{"data":{"player":null}}""");
        Assert.Empty(GameStatsDirectory.ParseStratzPositions(nobody.RootElement));
    }

    [Fact]
    public async Task TheMedalIsSavedDailySoItsChangeShows()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new SetStatsSharingCommand(Kai, true)); // Legend 5
        var today = DateOnly.FromDateTime(fixture.Clock.UtcNow.UtcDateTime);
        var first = GameStats.Read<DotaProfile>((await sender.Send(new GetMyStatsQuery(Kai))).Value.Dota)!;
        Assert.Equal([new DotaRankPoint(today, 45)], first.RankHistory);
        Assert.Null(GameStats.RankChange(first, today)); // nothing earlier to compare with yet

        // A refresh the same day replaces the day's medal; the next day adds one, and a failed fetch keeps the history.
        fixture.Stats.RankTier = 51;
        fixture.Clock.Advance(GameStats.RefreshAfter);
        await sender.Send(new RefreshMyStatsCommand(Kai));
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        fixture.Stats.RankTier = 52;
        await sender.Send(new RefreshMyStatsCommand(Kai));
        fixture.Stats.Fail = true;
        await sender.Send(new RefreshMyStatsCommand(Kai));
        var card = GameStats.Read<DotaProfile>((await sender.Send(new GetMyStatsQuery(Kai))).Value.Dota)!;
        Assert.Equal([new DotaRankPoint(today, 51), new DotaRankPoint(today.AddDays(1), 52)], card.RankHistory);
        Assert.Equal((1, today), GameStats.RankChange(card, today.AddDays(1)));
        // Legend 5 → Ancient 2 is two stars; points older than the window don't count.
        var longer = card with { RankHistory = [new DotaRankPoint(today.AddDays(-40), 35), new DotaRankPoint(today, 45)] };
        Assert.Equal((2, today), GameStats.RankChange(longer with { RankTier = 52 }, today.AddDays(1)));
        Assert.Null(GameStats.RankChange(card with { RankTier = null }, today.AddDays(1)));
        Assert.Equal((1, 20, 35), (GameStats.StarsOf(11), GameStats.StarsOf(45), GameStats.StarsOf(80)));
    }

    [Fact]
    public void DuosComeFromPeersAndGamesWithTheGroupFromRecaps()
    {
        long kai = GameStats.DotaAccountId(KaiSteam)!.Value, mira = GameStats.DotaAccountId(MiraSteam)!.Value;
        DotaProfile Profile(params DotaPeer[] peers) => new(0, null, 45, null, 1, 1, []) { Peers = peers };
        CachedStats Card(DotaProfile profile) => new(0, StatsGame.Dota2, StatsStatus.Ok, JsonSerializer.Serialize(profile, GameStats.Json), DateTimeOffset.UnixEpoch);
        PlayerStatsCard[] cards =
        [
            new(Kai, true, KaiSteam, Card(Profile(new DotaPeer(mira, 30, 18), new DotaPeer(999, 50, 25))), null),
            new(Mira, true, MiraSteam, Card(Profile(new DotaPeer(kai, 31, 19))), null),
            // Taro doesn't share: his peers aren't used and nobody is paired with him.
            new(Taro, false, "76561198000000003", Card(Profile(new DotaPeer(kai, 80, 40))), null)
        ];
        Assert.Equal([new DotaDuo(Kai, Mira, 31, 19)], GameStats.Duos(cards));

        PlayerMatch Line(ulong user, bool won, string id) => new(user, StatsGame.Dota2, id, DateTimeOffset.UnixEpoch, 1800, won, null, null, 1, 1, 1);
        var recaps = new[]
        {
            new GroupRecap(Guid.Empty, StatsGame.Dota2, "1", DateTimeOffset.UnixEpoch, 1800, [Line(Kai, true, "1"), Line(Mira, true, "1")]),
            new GroupRecap(Guid.Empty, StatsGame.Dota2, "2", DateTimeOffset.UnixEpoch, 1800, [Line(Kai, false, "2"), Line(Mira, true, "2")])
        };
        var with = GameStats.WithGroup(recaps);
        Assert.Equal(new WinRecord(2, 1), with[(Kai, StatsGame.Dota2)]);
        Assert.Equal(new WinRecord(2, 2), with[(Mira, StatsGame.Dota2)]);
    }

    [Fact]
    public void FaceitAnswersBecomeACard()
    {
        using var player = JsonDocument.Parse("""
            {"player_id":"abc-123","nickname":"kai","games":{"cs2":{"skill_level":8,"faceit_elo":1734,"game_player_id":"76561198000000001"}}}
            """);
        using var stats = JsonDocument.Parse("""
            {"lifetime":{"Matches":"412","Win Rate %":"53","Average K/D Ratio":"1.12","Average Headshots %":"48"},
             "segments":[{"label":"Mirage","type":"Map","stats":{"Matches":"120","Win Rate %":"57"}},
                         {"label":"Inferno","type":"Map","stats":{"Matches":"98","Win Rate %":"49"}},
                         {"label":"Nuke","type":"Map","stats":{"Matches":"12","Win Rate %":"40"}},
                         {"label":"5v5","type":"Mode","stats":{"Matches":"400"}}]}
            """);
        var card = GameStatsDirectory.ParseCs2(player.RootElement, stats.RootElement);
        Assert.Equal(("kai", 8, 1734, 412, 53.0, 1.12, 48.0), (card.Nickname, card.Level, card.Elo, card.Matches, card.WinRate, card.KillDeath, card.Headshots));
        Assert.Equal(["Mirage", "Inferno"], card.Maps.Select(map => map.Name));

        var noStats = GameStatsDirectory.ParseCs2(player.RootElement, null);
        Assert.Equal((8, 0, (double?)null), (noStats.Level, noStats.Matches, noStats.WinRate));
    }

    [Fact]
    public void GroupsGetCardsForTheirGamesAndIdsConvert()
    {
        Guid group = Guid.NewGuid();
        Assert.Equal([StatsGame.Dota2, StatsGame.Cs2], GameStats.GamesOf([
            new GroupGame(Guid.NewGuid(), group, "Counter-Strike 2", GroupGame.SteamProvider, "730"),
            new GroupGame(Guid.NewGuid(), group, "Dota 2", GroupGame.SteamProvider, "570"),
            new GroupGame(Guid.NewGuid(), group, "League of Legends", GroupGame.IgdbProvider, "115")]));
        Assert.Empty(GameStats.GamesOf([new GroupGame(Guid.NewGuid(), group, "Dota 2")]));
        Assert.Equal(111458067, GameStats.DotaAccountId("76561198071723795"));
        Assert.Null(GameStats.DotaAccountId("not a number"));
        Assert.Null(GameStats.DotaMedal(null));
        Assert.Equal((8, 0), GameStats.DotaMedal(80));
    }

    [Fact]
    public async Task NothingIsFetchedOrShownUntilAMemberShares()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new LinkSteamCommand(Mira, MiraSteam));

        Assert.Equal(0, (await sender.Send(new RefreshGameStatsCommand())).Value);
        Assert.Empty(fixture.Stats.Asked);
        var stats = (await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value;
        Assert.Equal([StatsGame.Dota2, StatsGame.Cs2], stats.Games);
        Assert.Equal((0, false), (stats.Sharing, stats.YouShare));
        Assert.All(stats.Cards, card => Assert.Null(card.SteamId));

        // Kai shares: fetched at once, visible to the group; Mira still isn't fetched.
        var mine = (await sender.Send(new SetStatsSharingCommand(Kai, true))).Value;
        Assert.Equal((true, StatsStatus.Ok, StatsStatus.NoAccount), (mine.Shares, mine.Dota!.Status, mine.Cs2!.Status));
        Assert.Equal([KaiSteam], fixture.Stats.Asked);
        var kai = (await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value.Cards.Single(card => card.UserId == Kai);
        Assert.Equal((true, 45), (kai.Shares, kai.DotaProfile!.RankTier));

        // Turning it off deletes the cards.
        await sender.Send(new SetStatsSharingCommand(Kai, false));
        kai = (await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value.Cards.Single(card => card.UserId == Kai);
        Assert.Equal((false, null), (kai.Shares, kai.Dota));
        Assert.Null((await sender.Send(new GetMyStatsQuery(Kai))).Value.Dota);
    }

    [Fact]
    public async Task IdenticalRequestsOnTheirWayShareOneCall()
    {
        using var directory = new GameStatsDirectory(new NoClients(), new Microsoft.Extensions.Configuration.ConfigurationManager(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GameStatsDirectory>.Instance);
        int sent = 0;
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Send() { Interlocked.Increment(ref sent); return answer.Task; }

        // Three callers ask for the same URL while it's on its way: one request; one caller giving up doesn't stop it.
        using var giveUp = new CancellationTokenSource();
        var first = directory.SharedAsync("players/1/wl", Send, CancellationToken.None);
        var second = directory.SharedAsync("players/1/wl", Send, CancellationToken.None);
        var leaving = directory.SharedAsync("players/1/wl", Send, giveUp.Token);
        var other = directory.SharedAsync("players/2/wl", () => Task.FromResult("other"), CancellationToken.None);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        answer.SetResult("{}");
        Assert.Equal(("{}", "{}", "other"), (await first, await second, await other));
        Assert.Equal(1, sent);

        // Once it's answered, the next ask is a new request; a failure reaches every waiting caller and isn't kept.
        var failing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = directory.SharedAsync("players/1/wl", () => { sent++; return failing.Task; }, CancellationToken.None);
        var b = directory.SharedAsync("players/1/wl", () => { sent++; return failing.Task; }, CancellationToken.None);
        failing.SetException(new InvalidOperationException("500"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        Assert.Equal("again", await directory.SharedAsync("players/1/wl", () => { sent++; return Task.FromResult("again"); }, CancellationToken.None));
        Assert.Equal(3, sent);
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No requests in this test.");
    }

    [Fact]
    public async Task TwinkStatsShowOneCardPerChosenAccountOrNone()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        const string Twink = PlayerGameTests.TwinkSteam;
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new LinkSteamCommand(Kai, Twink, Twink: true));
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        Assert.Equal([KaiSteam, Twink], fixture.Stats.Asked);

        var stats = (await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value;
        var kai = stats.Cards.Where(card => card.UserId == Kai).ToArray();
        Assert.Equal([(KaiSteam, true), (Twink, false)], kai.Select(card => (card.SteamId, card.IsMain)));
        Assert.All(kai, card => Assert.Equal(45, card.DotaProfile!.RankTier));
        Assert.Equal((1, 3), (stats.Sharing, stats.MemberCount));

        // Only the twink: the main account's cards go away and nothing new is fetched for the twink.
        fixture.Stats.Asked.Clear();
        var mine = (await sender.Send(new SetStatsAccountsCommand(Kai, [Twink]))).Value;
        Assert.Empty(fixture.Stats.Asked);
        Assert.Equal([(KaiSteam, false, false), (Twink, true, true)], mine.Accounts.Select(item => (item.SteamId, item.Show, item.Dota is not null)));
        Assert.Equal((true, Twink), (mine.Shares, mine.SteamId));
        stats = (await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value;
        Assert.Equal([Twink], stats.Cards.Where(card => card.UserId == Kai).Select(card => card.SteamId));

        // Showing the main account again fetches it; none turns sharing off and deletes everything.
        await sender.Send(new SetStatsAccountsCommand(Kai, [KaiSteam, Twink]));
        Assert.Equal([KaiSteam], fixture.Stats.Asked);
        mine = (await sender.Send(new SetStatsAccountsCommand(Kai, []))).Value;
        Assert.False(mine.Shares);
        Assert.All(mine.Accounts, item => Assert.Equal((false, (CachedStats?)null), (item.Show, item.Dota)));
        Assert.Single((await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value.Cards, card => card.UserId == Kai);

        // The switch turns every account back on when none was chosen.
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        Assert.All((await sender.Send(new GetMyStatsQuery(Kai))).Value.Accounts, item => Assert.True(item.Show && item.Dota is not null));
    }

    [Fact]
    public async Task RefreshMeReloadsOnlyYourCardsAndTheBotRefreshesHourly()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new LinkSteamCommand(Mira, MiraSteam));
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        await sender.Send(new SetStatsSharingCommand(Mira, true));
        fixture.Stats.Asked.Clear();

        // A click right after a good load keeps the cards; a minute later only the clicker's are fetched.
        await sender.Send(new RefreshMyStatsCommand(Kai));
        Assert.Empty(fixture.Stats.Asked);
        fixture.Clock.Advance(GameStats.RefreshAfter);
        await sender.Send(new RefreshMyStatsCommand(Kai));
        Assert.Equal([KaiSteam], fixture.Stats.Asked);

        // A failed load can be retried at once.
        fixture.Stats.Fail = true;
        fixture.Clock.Advance(GameStats.RefreshAfter);
        await sender.Send(new RefreshMyStatsCommand(Kai));
        fixture.Stats.Fail = false;
        await sender.Send(new RefreshMyStatsCommand(Kai));
        Assert.Equal([KaiSteam, KaiSteam, KaiSteam], fixture.Stats.Asked);

        // Within the hour the bot leaves both alone; after it, both are reloaded (two cards each).
        fixture.Stats.Asked.Clear();
        Assert.Equal(0, (await sender.Send(new RefreshGameStatsCommand())).Value);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(4, (await sender.Send(new RefreshGameStatsCommand())).Value);
        Assert.Equal([KaiSteam, MiraSteam], fixture.Stats.Asked.Order());

        // When the day's OpenDota budget is spent, the bot waits; Refresh me still works.
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Stats.BackgroundBudgetLeft = false;
        Assert.Equal(0, (await sender.Send(new RefreshGameStatsCommand())).Value);
        fixture.Stats.Asked.Clear();
        await sender.Send(new RefreshMyStatsCommand(Mira));
        Assert.Equal([MiraSteam], fixture.Stats.Asked);
    }

    [Fact]
    public async Task RefreshTakesStaleCardsAndKeepsGoodDataWhenTheServiceFails()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        fixture.Stats.Asked.Clear();

        Assert.Equal(0, (await sender.Send(new RefreshGameStatsCommand())).Value);
        fixture.Clock.Advance(GameStats.FreshFor);
        fixture.Stats.Fail = true;
        Assert.Equal(2, (await sender.Send(new RefreshGameStatsCommand())).Value);
        var mine = (await sender.Send(new GetMyStatsQuery(Kai))).Value;
        Assert.Equal(StatsStatus.Error, mine.Dota!.Status);
        Assert.Equal(45, GameStats.Read<DotaProfile>(mine.Dota)!.RankTier);
    }

    [Fact]
    public void MatchAnswersBecomeLinesAndGamesTogether()
    {
        using var recent = JsonDocument.Parse("""
            [{"match_id":7001,"player_slot":2,"radiant_win":true,"duration":2280,"start_time":1795000000,"hero_id":14,"kills":12,"deaths":3,"assists":15},
             {"match_id":7002,"player_slot":130,"radiant_win":true,"duration":1900,"start_time":1794990000,"hero_id":99,"kills":1,"deaths":9,"assists":4}]
            """);
        var names = new Dictionary<int, (string Name, string? Image)> { [14] = ("Pudge", "https://cdn/pudge.png") };
        var dota = GameStatsDirectory.ParseDotaMatches(Kai, recent.RootElement, names);
        Assert.Equal(("7001", true, "Pudge", 12, 3, 15, 2280), (dota[0].MatchId, dota[0].Won, dota[0].Pick, dota[0].Kills, dota[0].Deaths, dota[0].Assists, dota[0].DurationSeconds));
        Assert.Equal((false, "#99"), (dota[1].Won, dota[1].Pick));

        using var history = JsonDocument.Parse("""
            {"items":[{"match_id":"1-abc","started_at":1795000000,"finished_at":1795002460,"results":{"winner":"faction2"},
              "teams":{"faction1":{"players":[{"player_id":"kai-id"}]},"faction2":{"players":[{"player_id":"mira-id"}]}}}]}
            """);
        var item = Assert.Single(GameStatsDirectory.FaceitHistory(history.RootElement));
        Assert.Equal(("1-abc", 2460, "faction1"), (item.MatchId, item.DurationSeconds, item.TeamOf["kai-id"]));
        using var stats = JsonDocument.Parse("""
            {"rounds":[{"round_stats":{"Map":"de_mirage","Score":"13 / 9"},"teams":[
              {"team_stats":{"Team Win":"1"},"players":[{"player_id":"kai-id","player_stats":{"Kills":"24","Deaths":"15","Assists":"4","Headshots %":"52"}}]},
              {"team_stats":{"Team Win":"0"},"players":[{"player_id":"mira-id","player_stats":{"Kills":"10","Deaths":"20","Assists":"2","Headshots %":"30"}}]}]}]}
            """);
        var cs2 = GameStatsDirectory.ParseFaceitMatch(Kai, "kai-id", item, stats.RootElement)!;
        Assert.Equal(("de_mirage", "13 / 9", true, 24, 52.0), (cs2.Pick, cs2.Score, cs2.Won, cs2.Kills, cs2.Headshots));
        Assert.Null(GameStatsDirectory.ParseFaceitMatch(Kai, "someone-else", item, stats.RootElement));

        var at = DateTimeOffset.FromUnixTimeSeconds(1795000000);
        PlayerMatch Line(ulong user, string id, bool won, StatsGame game = StatsGame.Dota2) => new(user, game, id, at, 1800, won, null, null, 1, 1, 1);
        var recaps = MatchRecaps.Find(Guid.Empty, [Kai, Mira, Taro],
            [Line(Kai, "A", true), Line(Mira, "A", true), Line(Kai, "B", true), Line(Kai, "C", true), Line(Mira, "C", false), Line(Taro, "D", true),
             Line(20000, "D", true), Line(Kai, "E", true, StatsGame.Cs2), Line(Mira, "E", true, StatsGame.Cs2)],
            [StatsGame.Dota2]);
        Assert.Equal([("A", (bool?)true), ("C", null)], recaps.Select(recap => (recap.MatchId, recap.Won)).OrderBy(item => item.MatchId));

        var text = HappyMix.Bot.Services.DiscordRecapPublisher.Text("Squad", recaps.Single(recap => recap.MatchId == "A") with
            { Players = [dota[0], dota[0] with { UserId = Mira, Pick = "Lion", Kills = 2 }] }, "en");
        Assert.Contains("**Squad** won a Dota 2 match together", text, StringComparison.Ordinal);
        Assert.Contains("<@10> Pudge · 12/3/15", text, StringComparison.Ordinal);
        Assert.Contains("https://www.dotabuff.com/matches/A", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GamesTogetherArePostedOnceAndOnlyFromSharers()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new LinkSteamCommand(Mira, MiraSteam));
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        var now = fixture.Clock.UtcNow;
        PlayerMatch Line(ulong user, string id, TimeSpan ago) => new(user, StatsGame.Dota2, id, now - ago, 2000, true, "Pudge", null, 5, 2, 7);
        fixture.Stats.Matches[Kai] = [Line(Kai, "fresh", TimeSpan.FromHours(1)), Line(Kai, "old", TimeSpan.FromDays(3))];
        fixture.Stats.Matches[Mira] = [Line(Mira, "fresh", TimeSpan.FromHours(1)), Line(Mira, "old", TimeSpan.FromDays(3))];
        await sender.Send(new SetRecapChannelCommand(Actor(Kai), groupId, true));

        // Mira doesn't share: her matches are never read, so there is no game together.
        Assert.Equal(0, (await sender.Send(new RefreshMatchesCommand([Guild]))).Value);
        Assert.Empty((await sender.Send(new GetGroupStatsQuery(Actor(Kai), groupId))).Value.Recaps);

        await sender.Send(new SetStatsSharingCommand(Mira, true));
        Assert.Equal(1, (await sender.Send(new RefreshMatchesCommand([Guild]))).Value);
        Assert.Equal("fresh", Assert.Single(fixture.Posted.Recaps).MatchId);
        var stats = (await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value;
        Assert.Equal(["fresh", "old"], stats.Recaps.Select(recap => recap.MatchId));
        Assert.True(stats.PostRecaps);

        // Read again later: nothing new is posted twice.
        fixture.Clock.Advance(MatchRecaps.FetchEvery);
        Assert.Equal(0, (await sender.Send(new RefreshMatchesCommand([Guild]))).Value);
        Assert.True((await sender.Send(new SetRecapChannelCommand(Actor(Mira), groupId, false))).IsFailure);

        // Unsharing deletes Mira's matches, and the game together goes with them.
        await sender.Send(new SetStatsSharingCommand(Mira, false));
        Assert.Empty((await sender.Send(new GetGroupStatsQuery(Actor(Kai), groupId))).Value.Recaps);
    }

    [Fact]
    public async Task GamesTogetherShowTheWholeMatchWithMembersMarked()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new LinkSteamCommand(Mira, MiraSteam));
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        await sender.Send(new SetStatsSharingCommand(Mira, true));
        var now = fixture.Clock.UtcNow;
        fixture.Stats.Matches[Kai] = [new(Kai, StatsGame.Dota2, "7001", now.AddHours(-1), 2000, true, "Pudge", null, 12, 3, 15),
            new(Kai, StatsGame.Dota2, "7000", now.AddDays(-2), 2000, true, "Lion", null, 1, 1, 1)];
        fixture.Stats.Matches[Mira] = [new(Mira, StatsGame.Dota2, "7001", now.AddHours(-1), 2000, true, "Lion", null, 2, 8, 20),
            new(Mira, StatsGame.Dota2, "7000", now.AddDays(-2), 2000, true, "Pudge", null, 1, 1, 1)];
        MatchParticipant Player(int side, string? name, string hero, int kills) =>
            new(side, null, name, hero, null, kills, 3, 15) { NetWorth = 16357, GoldPerMinute = 586 };
        fixture.Stats.Details["7001"] = new MatchDetail(StatsGame.Dota2, "7001", 0, [new(0, "Radiant", 34), new(1, "Dire", 28)],
            [Player(0, "stranger", "Axe", 4), Player(0, "kai", "Pudge", 12), Player(0, null, "Lion", 2), Player(1, "enemy", "Sniper", 9)], "all_pick", true);
        await sender.Send(new SetRecapChannelCommand(Actor(Kai), groupId, true));

        // The job reads each match shown on the Stats tab once; one without public data is remembered too.
        await sender.Send(new RefreshMatchesCommand([Guild]));
        Assert.Equal(["7001", "7000"], fixture.Stats.DetailsAsked);
        var posted = Assert.Single(fixture.Posted.Recaps);
        Assert.Equal(4, posted.Detail!.Players.Count);
        var stats = (await sender.Send(new GetGroupStatsQuery(Actor(Mira), groupId))).Value;
        var recap = stats.Recaps[0];
        Assert.Equal(("7001", 34), (recap.MatchId, recap.Detail!.Sides[0].Score));
        Assert.Null(stats.Recaps[1].Detail);
        Assert.Equal(new Dictionary<int, ulong> { [1] = Kai, [2] = Mira }, MatchRecaps.Members(recap));

        fixture.Clock.Advance(MatchRecaps.FetchEvery);
        await sender.Send(new RefreshMatchesCommand([Guild]));
        Assert.Equal(2, fixture.Stats.DetailsAsked.Count);

        string text = HappyMix.Bot.Services.DiscordRecapPublisher.Text("Squad", posted, "en");
        Assert.Contains("Radiant **34 : 28** Dire · Ranked · All Pick", text, StringComparison.Ordinal);
        Assert.Contains("<@10> Pudge · 12/3/15 · 16.4k NW · 586 GPM", text, StringComparison.Ordinal);
        Assert.Contains("https://stratz.com/matches/7001", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WholeMatchesWaitWhenTheDailyBudgetIsUsedUp()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await fixture.CreateGroupAsync();
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new LinkSteamCommand(Mira, MiraSteam));
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        await sender.Send(new SetStatsSharingCommand(Mira, true));
        await sender.Send(new RefreshPlayerMatchesCommand(Kai));
        var now = fixture.Clock.UtcNow;
        fixture.Stats.Matches[Kai] = [new(Kai, StatsGame.Dota2, "7001", now.AddHours(-1), 2000, true, "Pudge", null, 1, 1, 1)];
        fixture.Stats.Matches[Mira] = [new(Mira, StatsGame.Dota2, "7001", now.AddHours(-1), 2000, true, "Lion", null, 1, 1, 1)];
        await sender.Send(new RefreshPlayerMatchesCommand(Kai));
        await sender.Send(new RefreshPlayerMatchesCommand(Mira));
        fixture.Stats.BackgroundBudgetLeft = false;
        await sender.Send(new RefreshMatchesCommand([Guild]));
        Assert.Empty(fixture.Stats.DetailsAsked);
    }

    [Fact]
    public void WholeMatchAnswersListEveryPlayer()
    {
        var names = new Dictionary<int, (string Name, string? Image)> { [14] = ("Pudge", "https://cdn/pudge.png"), [26] = ("Lion", null) };
        using var stratz = JsonDocument.Parse("""
            {"id":7001,"didRadiantWin":false,"gameMode":"ALL_PICK_RANKED","lobbyType":"RANKED","radiantKills":[1,2,3],"direKills":[4,5],"players":[
             {"steamAccountId":41,"isRadiant":true,"heroId":14,"kills":12,"deaths":3,"assists":15,"networth":16357,"goldPerMinute":586,"experiencePerMinute":747,
              "numLastHits":238,"numDenies":2,"heroDamage":11388,"towerDamage":10053,"heroHealing":0,"level":18,"position":"POSITION_3","steamAccount":{"name":"kai","isAnonymous":false}},
             {"steamAccountId":42,"isRadiant":false,"heroId":26,"kills":2,"deaths":8,"assists":20,"position":"POSITION_5","steamAccount":{"name":"hidden","isAnonymous":true}}]}
            """);
        var match = GameStatsDirectory.ParseStratzMatch(stratz.RootElement, names)!;
        Assert.Equal(("7001", 1, 6, 9, "all_pick", true), (match.MatchId, match.WinningSide, match.Sides[0].Score, match.Sides[1].Score, match.Mode, match.Ranked));
        Assert.Equal(("kai", "Pudge", 3, 16357, 586, 747, 238), (match.Players[0].Name, match.Players[0].Pick, match.Players[0].Position,
            match.Players[0].NetWorth, match.Players[0].GoldPerMinute, match.Players[0].XpPerMinute, match.Players[0].LastHits));
        Assert.Equal((1, (string?)null, "Lion"), (match.Players[1].Side, match.Players[1].Name, match.Players[1].Pick)); // private profile: no name
        Assert.Null(GameStatsDirectory.ParseStratzMatch(JsonDocument.Parse("null").RootElement, names));

        using var openDota = JsonDocument.Parse("""
            {"match_id":7001,"radiant_win":true,"radiant_score":30,"dire_score":12,"game_mode":23,"lobby_type":0,"players":[
             {"account_id":41,"personaname":"kai","isRadiant":true,"hero_id":14,"kills":5,"deaths":1,"assists":7,"net_worth":9000,"gold_per_min":600},
             {"account_id":null,"personaname":"ghost","isRadiant":false,"hero_id":26,"kills":0,"deaths":5,"assists":1}]}
            """);
        match = GameStatsDirectory.ParseOpenDotaMatch(openDota.RootElement, names)!;
        Assert.Equal((0, 30, 12, "turbo", false), (match.WinningSide, match.Sides[0].Score, match.Sides[1].Score, match.Mode, match.Ranked));
        Assert.Equal(("kai", 9000, (string?)null), (match.Players[0].Name, match.Players[0].NetWorth, match.Players[1].Name));

        using var faceit = JsonDocument.Parse("""
            {"rounds":[{"round_stats":{"Map":"de_mirage","Score":"13 / 9"},"teams":[
              {"team_stats":{"Team":"team_kai","Final Score":"13","Team Win":"1"},"players":[
                {"player_id":"kai-id","nickname":"kai","player_stats":{"Kills":"24","Deaths":"15","Assists":"4","Headshots %":"52","ADR":"91.5","MVPs":"5"}}]},
              {"team_stats":{"Team":"team_x","Final Score":"9","Team Win":"0"},"players":[
                {"player_id":"x-id","nickname":"x","player_stats":{"Kills":"10","Deaths":"20","Assists":"2"}}]}]}]}
            """);
        match = GameStatsDirectory.ParseFaceitDetail("1-abc", faceit.RootElement)!;
        Assert.Equal(("de_mirage", 0, "team_kai", 13, 9), (match.Map, match.WinningSide, match.Sides[0].Name, match.Sides[0].Score, match.Sides[1].Score));
        Assert.Equal(("kai", 91.5, 5, 1), (match.Players[0].Name, match.Players[0].Adr, match.Players[0].Mvps, match.Players[1].Side));
    }

    [Fact]
    public void AWeekOfGamesTogetherIsSummed()
    {
        var at = new DateTimeOffset(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
        PlayerMatch Line(ulong user, string id, bool won, string hero, int kills = 5) => new(user, StatsGame.Dota2, id, at, 1800, won, hero, null, kills, 3, 6);
        var recaps = MatchRecaps.Find(Guid.Empty, [Kai, Mira],
            [Line(Kai, "1", true, "Pudge"), Line(Mira, "1", true, "Lion"),
             Line(Kai, "2", true, "Pudge"), Line(Mira, "2", true, "Lina"),
             Line(Kai, "3", false, "Pudge"), Line(Mira, "3", false, "Lion"),
             Line(Kai, "4", true, "Axe"), Line(Mira, "4", false, "Lion")],
            [StatsGame.Dota2]);
        var week = Assert.Single(WeeklyDigests.Build(recaps));
        Assert.Equal((4, 2, 1), (week.Games, week.Wins, week.Losses));
        Assert.Equal(("Lion", 3), (week.MostPlayed, week.MostPlayedCount));
        Assert.Equal((Kai, 3, 4), (week.Best!.UserId, week.Best.Wins, week.Best.Games));
        Assert.Equal(new DateOnly(2026, 11, 16), WeeklyDigests.WeekStart(new DateTime(2026, 11, 22, 23, 0, 0)));
        Assert.Equal(new DateOnly(2026, 11, 23), WeeklyDigests.WeekStart(new DateTime(2026, 11, 23, 0, 0, 0)));

        var text = HappyMix.Bot.Services.DiscordRecapPublisher.DigestText("Squad", new DateOnly(2026, 11, 16), [week], "en");
        Assert.Contains("**Squad** · week of 16 November", text, StringComparison.Ordinal);
        Assert.Contains("4 games together · 2W 1L", text, StringComparison.Ordinal);
        Assert.Contains("best: <@10> won 3 of 4", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWeeklySummaryIsPostedOnMondayOncePerWeekWhenTurnedOn()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        await sender.Send(new LinkSteamCommand(Mira, MiraSteam));
        await sender.Send(new SetStatsSharingCommand(Kai, true));
        await sender.Send(new SetStatsSharingCommand(Mira, true));
        // Sunday 22 November: two games together this week (Monday 16 – Sunday 22), one of them won.
        var now = fixture.Clock.UtcNow;
        PlayerMatch Line(ulong user, string id, bool won) => new(user, StatsGame.Dota2, id, now.AddDays(-2), 2000, won, "Pudge", null, 5, 2, 7);
        fixture.Stats.Matches[Kai] = [Line(Kai, "w", true), Line(Kai, "l", false)];
        fixture.Stats.Matches[Mira] = [Line(Mira, "w", true), Line(Mira, "l", false)];
        await sender.Send(new RefreshMatchesCommand([Guild]));
        Assert.Equal((2, 1, 1), (await sender.Send(new GetGroupStatsQuery(Actor(Kai), groupId))).Value.Week.Single() is var week
            ? (week.Games, week.Wins, week.Losses) : default);

        // Not Monday yet, and off: nothing.
        Assert.Equal(0, (await sender.Send(new PostWeeklyDigestsCommand([Guild], "Europe/Kyiv"))).Value);
        await sender.Send(new SetRecapChannelCommand(Actor(Kai), groupId, false, Digest: true));
        Assert.True((await sender.Send(new GetGroupStatsQuery(Actor(Kai), groupId))).Value.PostDigest);

        // Monday 23 November, 09:00 UTC = 11:00 in Kyiv: last week's summary, once.
        fixture.Clock.Advance(TimeSpan.FromHours(21));
        Assert.Equal(1, (await sender.Send(new PostWeeklyDigestsCommand([Guild], "Europe/Kyiv"))).Value);
        var (weekStart, digests) = Assert.Single(fixture.Posted.Digests);
        Assert.Equal((new DateOnly(2026, 11, 16), 2), (weekStart, digests.Single().Games));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await sender.Send(new PostWeeklyDigestsCommand([Guild], "Europe/Kyiv"))).Value);

        // A week with no games together isn't posted.
        fixture.Clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(0, (await sender.Send(new PostWeeklyDigestsCommand([Guild], "Europe/Kyiv"))).Value);
    }

    private static GroupActor Actor(ulong user) => new(Guild, user, false);

    private sealed class FakeStats : IGameStatsDirectory
    {
        public List<string> Asked { get; } = [];
        public bool Fail { get; set; }
        public int RankTier { get; set; } = 45;
        public bool Cs2Enabled => true;
        public bool BackgroundBudgetLeft { get; set; } = true;

        public Task<StatsFetch<DotaProfile>> GetDotaAsync(string steamId64, CancellationToken cancellationToken)
        {
            Asked.Add(steamId64);
            return Task.FromResult(Fail ? new StatsFetch<DotaProfile>(StatsStatus.Error, null)
                : new StatsFetch<DotaProfile>(StatsStatus.Ok, new DotaProfile(GameStats.DotaAccountId(steamId64)!.Value, "kai", RankTier, null, 10, 5,
                    [new DotaHero(14, "Pudge", null, 8, 5)])));
        }

        public Task<StatsFetch<Cs2Profile>> GetCs2Async(string steamId64, CancellationToken cancellationToken) =>
            Task.FromResult(new StatsFetch<Cs2Profile>(Fail ? StatsStatus.Error : StatsStatus.NoAccount, null));

        /// <summary>Matches each person "played", by Discord account.</summary>
        public Dictionary<ulong, List<PlayerMatch>> Matches { get; } = [];

        public Task<StatsFetch<IReadOnlyList<PlayerMatch>>> GetMatchesAsync(ulong userId, StatsGame game, string steamId64, IReadOnlySet<string> known,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StatsFetch<IReadOnlyList<PlayerMatch>>(StatsStatus.Ok,
                Matches.GetValueOrDefault(userId)?.Where(match => match.Game == game).ToArray() ?? []));

        /// <summary>Whole matches by id; any other match has no public data.</summary>
        public Dictionary<string, MatchDetail> Details { get; } = [];
        public List<string> DetailsAsked { get; } = [];

        public Task<StatsFetch<MatchDetail>> GetMatchDetailAsync(StatsGame game, string matchId, CancellationToken cancellationToken)
        {
            DetailsAsked.Add(matchId);
            return Task.FromResult(Details.TryGetValue(matchId, out var detail)
                ? new StatsFetch<MatchDetail>(StatsStatus.Ok, detail) : new StatsFetch<MatchDetail>(StatsStatus.NoPublicData, null));
        }
    }

    private sealed class Posted : IRecapPublisher
    {
        public List<GroupRecap> Recaps { get; } = [];

        public Task PublishAsync(ulong guildId, string groupName, GroupRecap recap, CancellationToken cancellationToken)
        {
            Recaps.Add(recap);
            return Task.CompletedTask;
        }

        public List<(DateOnly Week, IReadOnlyList<GroupDigest> Digests)> Digests { get; } = [];

        public Task PublishDigestAsync(ulong guildId, Guid groupId, string groupName, DateOnly weekStart, IReadOnlyList<GroupDigest> digests,
            CancellationToken cancellationToken)
        {
            Digests.Add((weekStart, digests));
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public LiveTests.MutableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        public FakeStats Stats { get; } = new();
        public Posted Posted { get; } = new();
        public ISender Sender => application.GetRequiredService<ISender>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton(storage.GetRequiredService<IPlayerGameStore>())
                .AddSingleton(storage.GetRequiredService<IGameStatsStore>())
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton<ISteamDirectory>(new PlayerGameTests.FakeSteam())
                .AddSingleton<IGameStatsDirectory>(fixture.Stats)
                .AddSingleton<IRecapPublisher>(fixture.Posted)
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        /// <summary>"Squad": Kai organizes, Mira and Taro are members; it plays Dota 2 and CS2.</summary>
        public async Task<Guid> CreateGroupAsync()
        {
            var groups = database.Services.GetRequiredService<IGuildGroupStore>();
            await groups.UpdateAsync(Guild, "Squad", _ => new GuildGroup("Squad", Kai, [Kai, Mira, Taro], []));
            var groupId = (await groups.ListAsync(Guild)).Single().Id;
            await groups.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), groupId, "Dota 2", GroupGame.SteamProvider, "570"));
            await groups.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), groupId, "Counter-Strike 2", GroupGame.SteamProvider, "730"));
            return groupId;
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }
}
