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
        public bool Cs2Enabled => true;

        public Task<StatsFetch<DotaProfile>> GetDotaAsync(string steamId64, CancellationToken cancellationToken)
        {
            Asked.Add(steamId64);
            return Task.FromResult(Fail ? new StatsFetch<DotaProfile>(StatsStatus.Error, null)
                : new StatsFetch<DotaProfile>(StatsStatus.Ok, new DotaProfile(GameStats.DotaAccountId(steamId64)!.Value, "kai", 45, null, 10, 5,
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
