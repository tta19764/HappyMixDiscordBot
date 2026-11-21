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
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public LiveTests.MutableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        public FakeStats Stats { get; } = new();
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
