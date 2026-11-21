using System.Security.Claims;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Abstractions.Profiles;
using HappyMix.Application.Players;
using HappyMix.Application.Profiles;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Bot.Web;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Players;

/// <summary>The Discord profile saved at sign-in, and the My games Save button and sorting.</summary>
public sealed class ProfileAndMarksTests
{
    private const ulong Kai = 10;
    private const string KaiSteam = "76561198000000001";

    [Fact]
    public async Task SyncSavesTheProfileAndKeepsSteamWhenDiscordDoesNotAnswer()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;

        var first = (await sender.Send(new SaveDiscordProfileCommand(Kai, "kai", "Kai", "abc123", new DiscordSteamConnection(KaiSteam, "kai_s", true), true))).Value;
        Assert.Equal(("Kai", KaiSteam), (first.Name, first.Steam!.SteamId));

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        // Discord didn't return connections this time: the name updates, Steam stays.
        await sender.Send(new SaveDiscordProfileCommand(Kai, "kai2", null, null, null, ConnectionsChecked: false));
        var saved = (await sender.Send(new GetUserProfileQuery(Kai))).Value!;
        Assert.Equal(("kai2", "kai2", KaiSteam, true), (saved.UserName, saved.Name, saved.Steam?.SteamId, saved.ConnectionsChecked));
        Assert.True(saved.SyncedAt > first.SyncedAt);

        // Discord answered and Steam is gone: forget it.
        await sender.Send(new SaveDiscordProfileCommand(Kai, "kai2", null, null, null, ConnectionsChecked: true));
        Assert.Null((await sender.Send(new GetUserProfileQuery(Kai))).Value!.Steam);
        Assert.True((await sender.Send(new SaveDiscordProfileCommand(Kai, " ", null, null, null, true))).IsFailure);
    }

    [Fact]
    public void TheCookieCopyFollowsTheSavedProfile()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "10"), new Claim(ClaimTypes.Name, "old"),
            new Claim(DiscordConnections.SteamIdClaim, KaiSteam)], "test");
        var synced = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        DiscordProfileClaims.Apply(identity, new UserProfile(Kai, "kai", "Kai", "a_abc", null, true, synced));
        var user = new ClaimsPrincipal(identity);

        Assert.Equal("Kai", DiscordProfileClaims.Name(user));
        Assert.Single(identity.FindAll(ClaimTypes.Name));
        Assert.Null(DiscordConnections.FromUser(user));
        Assert.True(DiscordConnections.Checked(user));
        Assert.Equal(synced, DiscordProfileClaims.SyncedAt(user));
        Assert.Equal("https://cdn.discordapp.com/avatars/10/a_abc.gif?size=64", DiscordProfileClaims.AvatarUrl(user));
    }

    [Fact]
    public async Task SaveChangesOnlyWhatChangedAndSkipsGamesNoLongerListed()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        var dota = (await sender.Send(new AddPlayerGameCommand(Kai, "Dota 2", "570", null, null, GamePreference.Can))).Value;
        var tf2 = (await sender.Send(new AddPlayerGameCommand(Kai, "Team Fortress 2", "440", null, null, GamePreference.Can))).Value;

        var marks = new Dictionary<string, GamePreference> { [dota.Key] = GamePreference.Want, [tf2.Key] = GamePreference.Can, ["steam:1"] = GamePreference.Wont };
        Assert.Equal(1, (await sender.Send(new SetGamePreferencesCommand(Kai, marks))).Value);
        var games = (await sender.Send(new GetMyGamesQuery(Kai))).Value.Games.ToDictionary(game => game.Key, game => game.Preference);
        Assert.Equal((GamePreference.Want, GamePreference.Can), (games[dota.Key], games[tf2.Key]));
        Assert.True((await sender.Send(new SetGamePreferencesCommand(Kai, new Dictionary<string, GamePreference> { [dota.Key] = (GamePreference)7 }))).IsFailure);
    }

    [Fact]
    public void GamesSortByHoursNameOrMarkAndSteamGamesHaveArt()
    {
        var now = DateTimeOffset.UnixEpoch;
        PlayerGame Game(string name, GamePreference mark, int? hours, int? recent = null, string? appId = null) =>
            new(Kai, "name:" + name, name, appId is null ? null : "steam", appId, mark, appId is not null, hours * 60, recent, now);
        PlayerGame[] games = [Game("b", GamePreference.Wont, 300), Game("a", GamePreference.Can, 5, appId: "570"), Game("c", GamePreference.Want, null, 30)];

        Assert.Equal(["b", "a", "c"], games.Sorted("hours").Select(game => game.Name));
        Assert.Equal(["a", "b", "c"], games.Sorted("name").Select(game => game.Name));
        Assert.Equal(["c", "a", "b"], games.Sorted("mark").Select(game => game.Name));
        Assert.Equal("c", games.Sorted("recent").First().Name);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/570/header.jpg", MyGamesPage.Art(games[1]));
        Assert.Null(MyGamesPage.Art(games[0]));
    }

    [Fact]
    public async Task ImportTurnsAGameFromTheSearchIntoTheSteamGameKeepingItsMark()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await sender.Send(new AddPlayerGameCommand(Kai, "Dota 2", null, "igdb", "2963", GamePreference.Want));
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        fixture.Steam.Library = new SteamLibrary(SteamLibraryStatus.Ok, [new SteamOwnedGame("570", "Dota 2", 90556, 1608)]);

        Assert.Equal((0, 1), ((await sender.Send(new ImportSteamGamesCommand(Kai))).Value.Added, (await sender.Send(new ImportSteamGamesCommand(Kai))).Value.Updated));
        var dota = Assert.Single((await sender.Send(new GetMyGamesQuery(Kai))).Value.Games);
        Assert.Equal(("steam:570", "steam", GamePreference.Want, 90556), (dota.Key, dota.Provider, dota.Preference, dota.PlaytimeMinutes));
    }

    [Fact]
    public void RecentlyPlayedGamesFillWhatTheOwnedListLeavesOut()
    {
        // The owned list Steam returned for a real account left out Dota 2; the recently played list had it.
        using var owned = System.Text.Json.JsonDocument.Parse("""
            {"response":{"game_count":1,"games":[{"appid":730,"name":"Counter-Strike 2","playtime_forever":20574,"playtime_2weeks":105}]}}
            """);
        using var recent = System.Text.Json.JsonDocument.Parse("""
            {"response":{"total_count":2,"games":[{"appid":570,"name":"Dota 2","playtime_2weeks":1608,"playtime_forever":90556},
             {"appid":730,"name":"Counter-Strike 2","playtime_2weeks":105,"playtime_forever":20574}]}}
            """);
        var library = HappyMix.Infrastructure.Steam.SteamDirectory.WithRecent(
            HappyMix.Infrastructure.Steam.SteamDirectory.ParseLibrary(owned.RootElement),
            HappyMix.Infrastructure.Steam.SteamDirectory.ParseLibrary(recent.RootElement));
        Assert.Equal(["730", "570"], library.Games.Select(game => game.AppId));
        Assert.Equal(new SteamOwnedGame("570", "Dota 2", 90556, 1608), library.Games[1]);

        using var nothing = System.Text.Json.JsonDocument.Parse("""{"response":{}}""");
        Assert.Single(HappyMix.Infrastructure.Steam.SteamDirectory.WithRecent(
            HappyMix.Infrastructure.Steam.SteamDirectory.ParseLibrary(owned.RootElement),
            HappyMix.Infrastructure.Steam.SteamDirectory.ParseLibrary(nothing.RootElement)).Games);
    }

    [Fact]
    public void NewerSteamGamesGetThePictureTheStoreLists()
    {
        using var answer = System.Text.Json.JsonDocument.Parse("""
            {"3527290":{"success":true,"data":{"header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3527290/31bac6b2/header.jpg?t=1"}},
             "218":{"success":false},
             "666":{"success":true,"data":{"header_image":"https://evil.example/header.jpg"}}}
            """);
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3527290/31bac6b2/header.jpg?t=1",
            HappyMix.Bot.Services.SteamArt.Parse(answer.RootElement, "3527290"));
        Assert.Null(HappyMix.Bot.Services.SteamArt.Parse(answer.RootElement, "218"));
        Assert.Null(HappyMix.Bot.Services.SteamArt.Parse(answer.RootElement, "666"));
        Assert.Null(HappyMix.Bot.Services.SteamArt.Parse(answer.RootElement, "570"));
        Assert.True(HappyMix.Bot.Services.SteamArt.IsAppId("3527290"));
        Assert.False(HappyMix.Bot.Services.SteamArt.IsAppId("../x") || HappyMix.Bot.Services.SteamArt.IsAppId("0") || HappyMix.Bot.Services.SteamArt.IsAppId(""));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public LiveTests.MutableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        public ISender Sender => application.GetRequiredService<ISender>();
        public PlayerGameTests.FakeSteam Steam { get; } = new();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IUserProfileStore>())
                .AddSingleton(storage.GetRequiredService<IPlayerGameStore>())
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Guilds.IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Guilds.IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Guilds.IAccountLinkStore>())
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton<ISteamDirectory>(fixture.Steam)
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
