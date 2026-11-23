using System.Text.Json;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Players;
using HappyMix.Application.Stats;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Infrastructure.Stats;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Players;

/// <summary>Game features: the Dota 2 lane meta, its schedule and posting, and why stats lookups failed.</summary>
public sealed class GameFeatureTests
{
    private const ulong Guild = 1, Kai = 10;
    private const string KaiSteam = "76561198000000001";

    [Fact]
    public void LaneRolesAreSummedAndRankedByWinRateAmongCommonPicks()
    {
        // Pudge: 300 games over two buckets at 60%; Lina 400 games at 55%; Techies 20 games at 90% (too rare to rank).
        using var answer = JsonDocument.Parse("""
            [{"hero_id":14,"lane_role":2,"time":900,"games":"100","wins":"60"},{"hero_id":14,"lane_role":2,"time":1800,"games":"200","wins":"120"},
             {"hero_id":25,"lane_role":2,"time":1800,"games":"400","wins":"220"},
             {"hero_id":105,"lane_role":2,"time":1800,"games":"20","wins":"18"}]
            """);
        var names = new Dictionary<int, (string Name, string? Image)> { [14] = ("Pudge", "p.png"), [25] = ("Lina", null) };
        var heroes = GameStatsDirectory.ParseLaneRoles(answer.RootElement, names);
        Assert.Equal([("Pudge", 300, 60.0), ("Lina", 400, 55.0)], heroes.Select(hero => (hero.Name, hero.Games, hero.WinRate)));
    }

    [Fact]
    public void TwentyHeroesPerLaneFitTheDiscordPostWithIconsAndBuildLinks()
    {
        var heroes = Enumerable.Range(1, 25).Select(id => new MetaHero(id, "Keeper of the Light " + id, null, 123456, 70000)).ToArray();
        var meta = new DotaMeta(DateTimeOffset.UnixEpoch, new Dictionary<DotaLane, IReadOnlyList<MetaHero>>
            { [DotaLane.Safe] = heroes, [DotaLane.Mid] = heroes, [DotaLane.Off] = [] });
        var icons = Enumerable.Range(1, 25).ToDictionary(id => id, id => $"<:d2h{id}:1234567890123456789>");
        var messages = HappyMix.Bot.Services.DiscordDotaMetaPublisher.Messages(meta, 20, "en", icons);

        // A header, then one block per lane, split over messages Discord accepts.
        var embeds = messages.SelectMany(message => message).ToArray();
        Assert.Equal(4, embeds.Length);
        Assert.All(messages, message => Assert.True(message.Length <= 10 && message.Sum(embed => embed.Length) <= 6000));
        Assert.Equal(("Dota 2 · Meta heroes by lane", "Safe lane"), (embeds[0].Title, embeds[1].Title));
        var safe = embeds[1].Description.Split('\n');
        Assert.Equal(20, safe.Length);
        Assert.Equal("1. <:d2h1:1234567890123456789> **Keeper of the Light 1** 56.7% · 123,456 · [STRATZ](https://stratz.com/heroes/1/guides)"
            + " · [D2PT](https://dota2protracker.com/hero/Keeper%20of%20the%20Light%201)", safe[0]);
        Assert.Equal("—", embeds.Single(embed => embed.Title == "Off lane").Description);
        Assert.Equal("OpenDota", embeds[^1].Footer!.Value.Text);

        // Without icons (the bot couldn't make them) the line simply has none.
        var plain = HappyMix.Bot.Services.DiscordDotaMetaPublisher.Messages(meta, 5, "en");
        Assert.StartsWith("1. **Keeper of the Light 1** 56.7%", plain[0][1].Description, StringComparison.Ordinal);
        Assert.Equal(5, plain[0][1].Description.Split('\n').Length);
    }

    [Fact]
    public void BuildLinksAndHeroIconsUseTheRightAddresses()
    {
        Assert.Equal("https://stratz.com/heroes/86/guides", DotaBuilds.Stratz(86));
        Assert.Equal("https://dota2protracker.com/hero/Nature%27s%20Prophet", DotaBuilds.ProTracker("Nature's Prophet"));
        Assert.Equal("https://dota2protracker.com/hero/Anti-Mage", DotaBuilds.ProTracker("Anti-Mage"));
        Assert.Equal("https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/icons/lina.png",
            HappyMix.Bot.Services.HeroEmojis.IconUrl("https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/lina.png"));
        Assert.Null(HappyMix.Bot.Services.HeroEmojis.IconUrl(null));
        Assert.Equal((14, (int?)null, (int?)null), (HappyMix.Bot.Services.HeroEmojis.HeroId("d2h14"), HappyMix.Bot.Services.HeroEmojis.HeroId("pepe"),
            HappyMix.Bot.Services.HeroEmojis.HeroId("d2hx")));
    }

    [Fact]
    public void StratzQueriesDaysSinceThePatchForTheRoleAndRanks()
    {
        Assert.Equal("{ heroStats { winDay(gameModeIds: [ALL_PICK_RANKED], groupBy: HERO_ID, positionIds: [POSITION_1], bracketIds: [LEGEND, ANCIENT]) "
            + "{ day heroId winCount matchCount } } }",
            GameStatsDirectory.StratzQuery(new MetaFilter(MetaPatch.Current, 1, MetaRank.LegendAncient), daily: true));
        Assert.DoesNotContain("positionIds", GameStatsDirectory.StratzQuery(new MetaFilter(), daily: false), StringComparison.Ordinal);
        Assert.Contains("winWeek", GameStatsDirectory.StratzQuery(new MetaFilter(), daily: false), StringComparison.Ordinal);

        // 7.42 came out at 21:28 UTC on 18 November: counting starts with the 19th.
        var released = new DateTimeOffset(2026, 11, 18, 21, 28, 7, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), GameStatsDirectory.StartOf(released, daily: true));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero), GameStatsDirectory.StartOf(released.AddHours(-12), daily: true));
        using var patches = JsonDocument.Parse("""[{"name":"7.41","date":"2026-03-24T00:50:59.580Z","id":60},{"name":"7.42","date":"2026-11-18T21:28:07.019Z","id":61}]""");
        Assert.Equal(("7.42", released.AddMilliseconds(19)), GameStatsDirectory.LatestPatch(patches.RootElement));

        // Day rows: the 18th is before the patch day and left out; rows are summed per hero.
        long Day(int day) => new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        using var rows = JsonDocument.Parse($$"""
            [{"day":{{Day(18)}},"heroId":14,"winCount":900,"matchCount":1000},
             {"day":{{Day(19)}},"heroId":14,"winCount":330,"matchCount":600},{"day":{{Day(20)}},"heroId":14,"winCount":270,"matchCount":400},
             {"day":{{Day(19)}},"heroId":25,"winCount":5100,"matchCount":10000},{"day":{{Day(19)}},"heroId":105,"winCount":9,"matchCount":10}]
            """);
        var names = new Dictionary<int, (string Name, string? Image)> { [14] = ("Pudge", null), [25] = ("Lina", null) };
        var meta = GameStatsDirectory.ParseStratz(new MetaFilter(MetaPatch.Current, 2), rows.RootElement, "day",
            new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), names, "7.42", released, released.AddDays(2));
        Assert.Equal(["Pudge", "Lina"], meta.Heroes.Select(hero => hero.Name));
        Assert.Equal((1000, 600), (meta.Heroes[0].Games, meta.Heroes[0].Wins));
        Assert.Equal(5505, meta.Matches); // 11010 role picks, 2 per match
        Assert.Equal(1000 * 100.0 * 2 / 11010, meta.Heroes[0].PickRate, 3);
    }

    [Fact]
    public async Task TheSearchSortsCachesAndThePostGoesByRole()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        fixture.Heroes.Enabled = true;

        var page = (await sender.Send(new GetGameFeaturesQuery(Guild, Kai, new MetaFilter(MetaPatch.Current, 1, MetaRank.LegendAncient, MetaSort.PickRate)))).Value;
        Assert.True(page.SearchEnabled);
        Assert.Equal(["Axe", "Lina", "Pudge"], page.Search!.Heroes.Select(hero => hero.Name));
        Assert.Null(page.Heroes);
        // Another order of the same filter comes from the cache.
        var byScore = (await sender.Send(new GetGameFeaturesQuery(Guild, Kai, new MetaFilter(MetaPatch.Current, 1, MetaRank.LegendAncient)))).Value;
        Assert.Equal("Lina", byScore.Search!.Heroes[0].Name);
        Assert.Single(fixture.Heroes.Asked);
        // Any pick rate shows the rare hero too, still from the cache.
        var any = (await sender.Send(new GetGameFeaturesQuery(Guild, Kai, new MetaFilter(MetaPatch.Current, 1, MetaRank.LegendAncient, MinPickRate: 0)))).Value;
        Assert.Equal("Techies", any.Search!.Heroes[0].Name);
        Assert.Single(fixture.Heroes.Asked);

        await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Off, 99, DayOfWeek.Monday, 12, 10, MetaRank.DivineImmortal, MetaPatch.LastWeek, MetaSort.WinRate));
        Assert.True((await sender.Send(new PostDotaMetaCommand(Guild))).IsSuccess);
        var roles = Assert.Single(fixture.RolePosts);
        Assert.Equal([1, 2, 3, 4, 5], roles.Select(role => role.Filter.Position));
        Assert.All(roles, role => Assert.Equal((MetaRank.DivineImmortal, MetaPatch.LastWeek), (role.Filter.Rank, role.Filter.Patch)));
        Assert.Equal("Pudge", roles[0].Heroes[0].Name); // by win rate, Techies is under 1%
        Assert.Empty(fixture.Posted);
        // Posts can be set to any pick rate as well.
        await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Off, 99, DayOfWeek.Monday, 12, 10, MetaRank.DivineImmortal, MetaPatch.LastWeek, MetaSort.WinRate, 0));
        Assert.Equal(0, (await sender.Send(new GetDotaMetaSettingsQuery(Guild))).Value.MinPickRate);
        Assert.True((await sender.Send(new PostDotaMetaCommand(Guild))).IsSuccess);
        Assert.Equal("Techies", fixture.RolePosts[^1][0].Heroes[0].Name);
        Assert.True((await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Off, 99, DayOfWeek.Monday, 12, 10, MinPickRate: 7))).IsFailure);

        // STRATZ down and nothing cached for these filters: the post falls back to the lanes from OpenDota.
        fixture.Heroes.Fail = true;
        await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Off, 99, DayOfWeek.Monday, 12, 10, MetaRank.HeraldGuardian));
        Assert.True((await sender.Send(new PostDotaMetaCommand(Guild))).IsSuccess);
        Assert.Single(fixture.Posted);
        var failed = (await sender.Send(new GetGameFeaturesQuery(Guild, Kai, new MetaFilter(MetaPatch.Current, 3)))).Value;
        Assert.Equal(("STRATZ answered 503 Service Unavailable: busy", true), (failed.SearchError, failed.Heroes is not null));
    }

    [Fact]
    public void FiveRolesOfTwentyHeroesFitOneDiscordPost()
    {
        var heroes = Enumerable.Range(1, 20).Select(id => new RankedHero(id, "Keeper of the Light " + id, null, 123456, 70000, 12.5)).ToArray();
        var roles = Enumerable.Range(1, 5).Select(position => new HeroMeta(new MetaFilter(MetaPatch.Current, position), "7.42", null,
            DateTimeOffset.UnixEpoch, 100000, heroes)).ToArray();
        var messages = HappyMix.Bot.Services.DiscordDotaMetaPublisher.RoleMessages(roles, 20, "en");
        var embeds = messages.SelectMany(message => message).ToArray();
        Assert.Equal(6, embeds.Length);
        Assert.All(messages, message => Assert.True(message.Sum(embed => embed.Length) <= 6000, $"message length {message.Sum(embed => embed.Length)}"));
        Assert.StartsWith("Patch 7.42 · All ranks · Meta score", embeds[0].Description, StringComparison.Ordinal);
        Assert.Equal("Pos 1 · Carry", embeds[1].Title);
        Assert.Equal(20, embeds[1].Description.Split('\n').Length);
    }

    [Theory]
    [InlineData(MetaSchedule.Daily, "2026-11-23", 12, null, true)]
    [InlineData(MetaSchedule.Daily, "2026-11-23", 11, null, false)]
    [InlineData(MetaSchedule.Daily, "2026-11-23", 15, "2026-11-23", false)]
    [InlineData(MetaSchedule.EveryTwoDays, "2026-11-23", 12, "2026-11-22", false)]
    [InlineData(MetaSchedule.EveryTwoDays, "2026-11-23", 12, "2026-11-21", true)]
    [InlineData(MetaSchedule.Weekly, "2026-11-23", 12, "2026-11-16", true)]
    [InlineData(MetaSchedule.Weekly, "2026-11-24", 12, "2026-11-16", false)]
    [InlineData(MetaSchedule.Off, "2026-11-23", 12, null, false)]
    public void TheMetaIsDueByItsSchedule(MetaSchedule schedule, string today, int hour, string? last, bool due)
    {
        var settings = new DotaMetaSettings(Guild, schedule, 99, DayOfWeek.Monday, 12, null);
        Assert.Equal(due, DotaMetas.IsDue(settings, DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture), hour,
            last is null ? null : DateOnly.Parse(last, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(DotaMetas.IsDue(settings with { ChannelId = null }, DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture), hour, null));
    }

    [Fact]
    public async Task TheMetaIsPostedNowAndOnScheduleOnceADay()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Assert.Equal("Features.NoChannel", (await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Daily, null, DayOfWeek.Monday, 12))).Error.Code);
        Assert.True((await sender.Send(new PostDotaMetaCommand(Guild))).IsFailure);

        await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Daily, 99, DayOfWeek.Monday, 12));
        Assert.True((await sender.Send(new PostDotaMetaCommand(Guild))).IsSuccess);
        Assert.Equal((99UL, DotaMetas.DefaultCount), Assert.Single(fixture.Posted) is var post ? (post.Channel, post.Count) : default);

        // Sunday 22 November, 12:00 UTC = 14:00 in Kyiv: today's post already went out with Post now.
        Assert.Equal(0, (await sender.Send(new RunDotaMetaScheduleCommand([Guild], "Europe/Kyiv"))).Value);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, (await sender.Send(new RunDotaMetaScheduleCommand([Guild], "Europe/Kyiv"))).Value);
        Assert.Equal(0, (await sender.Send(new RunDotaMetaScheduleCommand([Guild], "Europe/Kyiv"))).Value);
        Assert.Equal(2, fixture.Source.Calls); // posts use data at most an hour old: once for Post now, once a day later

        var page = (await sender.Send(new GetGameFeaturesQuery(Guild, Kai))).Value;
        Assert.Equal(("Pudge", MetaSchedule.Daily, 10), (page.Heroes!.Lanes[DotaLane.Mid][0].Name, page.Meta.Schedule, page.Meta.Count));

        // Heroes per lane is the server's choice from 5, 10, 15 and 20.
        Assert.Equal(20, (await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Daily, 99, DayOfWeek.Monday, 12, 20))).Value.Count);
        Assert.True((await sender.Send(new SaveDotaMetaCommand(Guild, MetaSchedule.Daily, 99, DayOfWeek.Monday, 12, 7))).IsFailure);
        await sender.Send(new PostDotaMetaCommand(Guild));
        Assert.Equal(20, fixture.Posted[^1].Count);
    }

    [Fact]
    public async Task AFailedLookupKeepsItsReasonForTheCard()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        fixture.Stats.Reason = "OpenDota answered 500 Internal Server Error for players/39734273/wl: {\"error\":\"Internal Server Error\"}";
        var mine = (await sender.Send(new SetStatsSharingCommand(Kai, true, Guild))).Value;
        Assert.Equal((StatsStatus.Error, fixture.Stats.Reason), (mine.Dota!.Status, mine.Dota.Reason));

        fixture.Stats.Reason = null;
        fixture.Clock.Advance(GameStats.RefreshAfter);
        mine = (await sender.Send(new RefreshMyStatsCommand(Kai, Guild))).Value;
        Assert.Equal((StatsStatus.Ok, (string?)null), (mine.Dota!.Status, mine.Dota.Reason));

        Assert.Equal("FACEIT didn't answer within 10 seconds.", GameStatsDirectory.Reason("FACEIT", new TaskCanceledException()));
        Assert.Equal("OpenDota didn't answer within 45 seconds.", GameStatsDirectory.Reason("OpenDota", new TaskCanceledException()));
        Assert.Null(GameStatsDirectory.Reason("FACEIT", new InvalidOperationException()));
    }

    internal sealed class FakeHeroSource : IHeroMetaSource
    {
        public bool Enabled { get; set; }
        public bool Fail { get; set; }
        public List<MetaFilter> Asked { get; } = [];

        public Task<StatsFetch<HeroMeta>> GetHeroMetaAsync(MetaFilter filter, CancellationToken cancellationToken)
        {
            Asked.Add(filter);
            if (Fail)
                return Task.FromResult(new StatsFetch<HeroMeta>(StatsStatus.Error, null, "STRATZ answered 503 Service Unavailable: busy"));
            // Pudge: high win rate over few games; Lina: a little lower over many; Axe: popular and weak;
            // Techies: wins a lot but is rarely picked, so the default 1% minimum hides him.
            RankedHero[] heroes = [new(105, "Techies", null, 300, 240, 0.4), new(14, "Pudge", null, 120, 72, 1), new(25, "Lina", null, 20000, 11000, 12), new(2, "Axe", null, 40000, 18000, 25)];
            return Task.FromResult(new StatsFetch<HeroMeta>(StatsStatus.Ok,
                new HeroMeta(filter, "7.42", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 80000, heroes.OrderByDescending(hero => hero.Score).ToArray())));
        }
    }

    private sealed class FakeSource : IDotaMetaSource
    {
        public int Calls { get; private set; }
        /// <summary>The test clock, so fetched data is stamped with the test's time and not today's.</summary>
        public LiveTests.MutableClock? Clock { get; set; }

        public Task<StatsFetch<DotaMeta>> GetMetaAsync(CancellationToken cancellationToken)
        {
            Calls++;
            var lanes = new Dictionary<DotaLane, IReadOnlyList<MetaHero>>
            {
                [DotaLane.Safe] = [new MetaHero(1, "Anti-Mage", null, 500, 280)],
                [DotaLane.Mid] = [new MetaHero(14, "Pudge", null, 300, 180)],
                [DotaLane.Off] = []
            };
            return Task.FromResult(new StatsFetch<DotaMeta>(StatsStatus.Ok, new DotaMeta(Clock?.UtcNow ?? DateTimeOffset.UtcNow, lanes)));
        }
    }

    private sealed class FakePublisher(List<(ulong Guild, ulong Channel, int Count)> posted, List<IReadOnlyList<HeroMeta>> roles) : IDotaMetaPublisher
    {
        public Task<string?> PublishRolesAsync(ulong guildId, ulong channelId, IReadOnlyList<HeroMeta> metas, int count, CancellationToken cancellationToken)
        {
            roles.Add(metas);
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PublishAsync(ulong guildId, ulong channelId, DotaMeta meta, int count, CancellationToken cancellationToken)
        {
            posted.Add((guildId, channelId, count));
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeStats : IGameStatsDirectory
    {
        public string? Reason { get; set; }
        public bool Cs2Enabled => false;

        public Task<StatsFetch<DotaProfile>> GetDotaAsync(string steamId64, CancellationToken cancellationToken) =>
            Task.FromResult(Reason is { } reason ? new StatsFetch<DotaProfile>(StatsStatus.Error, null, reason)
                : new StatsFetch<DotaProfile>(StatsStatus.Ok, new DotaProfile(1, "kai", 45, null, 1, 1, [])));

        public Task<StatsFetch<Cs2Profile>> GetCs2Async(string steamId64, CancellationToken cancellationToken) =>
            Task.FromResult(new StatsFetch<Cs2Profile>(StatsStatus.NoAccount, null));

        public Task<StatsFetch<IReadOnlyList<PlayerMatch>>> GetMatchesAsync(ulong userId, StatsGame game, string steamId64, IReadOnlySet<string> known,
            CancellationToken cancellationToken) => Task.FromResult(new StatsFetch<IReadOnlyList<PlayerMatch>>(StatsStatus.Ok, []));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public LiveTests.MutableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        public FakeSource Source { get; } = new();

        private Fixture() => Source.Clock = Clock;
        public FakeStats Stats { get; } = new();
        public List<(ulong Guild, ulong Channel, int Count)> Posted { get; } = [];
        public List<IReadOnlyList<HeroMeta>> RolePosts { get; } = [];
        public FakeHeroSource Heroes { get; } = new();
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
                .AddSingleton(storage.GetRequiredService<IGameFeatureStore>())
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton<ISteamDirectory>(new PlayerGameTests.FakeSteam())
                .AddSingleton<IGameStatsDirectory>(fixture.Stats)
                .AddSingleton<IDotaMetaSource>(fixture.Source)
                .AddSingleton<IDotaMetaPublisher>(new FakePublisher(fixture.Posted, fixture.RolePosts))
                .AddSingleton<IHeroMetaSource>(fixture.Heroes)
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }
}
