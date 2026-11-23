using System.Text.Json;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Guilds;
using HappyMix.Application.Players;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Bot.Web;
using HappyMix.Infrastructure.Steam;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Players;

/// <summary>My games: Steam links, library import, Want / Can / Won't marks and what groups see.</summary>
public sealed class PlayerGameTests
{
    private const ulong Guild = 1, Kai = 10, Mira = 20, Taro = 30;
    private const string KaiSteam = "76561198000000001", MiraSteam = "76561198000000002";
    internal const string TwinkSteam = "76561198000000003";

    [Fact]
    public async Task TwinkAccountsAreLinkedNamedSwappedRemovedAndImportedTogether()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));
        var twink = (await sender.Send(new LinkSteamCommand(Kai, TwinkSteam, Twink: true))).Value;
        Assert.False(twink.IsMain);
        var accounts = (await sender.Send(new GetMyGamesQuery(Kai))).Value.Accounts;
        Assert.Equal([(KaiSteam, true), (TwinkSteam, false)], accounts.Select(account => (account.SteamId, account.IsMain)));

        // One library from both accounts: Dota 2 is one game with the hours added up.
        fixture.Steam.Library = new(SteamLibraryStatus.Ok, [new SteamOwnedGame("570", "Dota 2", 100, 10)]);
        fixture.Steam.Libraries[TwinkSteam] = new(SteamLibraryStatus.Ok, [new SteamOwnedGame("570", "Dota 2", 50, 5), new SteamOwnedGame("1145360", "Hades", 30, 0)]);
        var import = (await sender.Send(new ImportSteamGamesCommand(Kai))).Value;
        Assert.Equal((2, 2, 0), (import.Added, import.Total, import.Skipped));
        var games = (await sender.Send(new GetMyGamesQuery(Kai))).Value.Games;
        Assert.Equal((150, 15), games.Single(game => game.Key == "steam:570") is var dota ? (dota.PlaytimeMinutes, dota.RecentMinutes) : default);
        // A private twink is skipped; the main library still imports.
        fixture.Steam.Libraries[TwinkSteam] = new(SteamLibraryStatus.Private, []);
        Assert.Equal(1, (await sender.Send(new ImportSteamGamesCommand(Kai))).Value.Skipped);

        Assert.True((await sender.Send(new SetSteamLabelCommand(Kai, TwinkSteam, "  Party   smurf "))).IsSuccess);
        Assert.True((await sender.Send(new SetSteamLabelCommand(Kai, TwinkSteam, new string('x', 33)))).IsFailure);
        Assert.True((await sender.Send(new SetMainSteamCommand(Kai, TwinkSteam))).IsSuccess);
        Assert.True((await sender.Send(new SetMainSteamCommand(Kai, MiraSteam))).IsFailure);
        accounts = (await sender.Send(new GetMyGamesQuery(Kai))).Value.Accounts;
        Assert.Equal([(TwinkSteam, true, "Party smurf"), (KaiSteam, false, (string?)null)], accounts.Select(account => (account.SteamId, account.IsMain, account.Label)));

        // Removing the main account makes the remaining one main; the imported games stay.
        await sender.Send(new UnlinkSteamCommand(Kai, TwinkSteam));
        var left = (await sender.Send(new GetMyGamesQuery(Kai))).Value;
        Assert.Equal((KaiSteam, true), (Assert.Single(left.Accounts).SteamId, left.Accounts[0].IsMain));
        Assert.Contains(left.Games, game => game.Key == "steam:1145360");

        // A new main account next to a twink replaces only the old main one.
        await sender.Send(new LinkSteamCommand(Kai, TwinkSteam, Twink: true));
        await sender.Send(new LinkSteamCommand(Kai, MiraSteam));
        accounts = (await sender.Send(new GetMyGamesQuery(Kai))).Value.Accounts;
        Assert.Equal([(MiraSteam, true), (TwinkSteam, false)], accounts.Select(account => (account.SteamId, account.IsMain)));
        Assert.True((await sender.Send(new LinkSteamCommand(Mira, TwinkSteam, Twink: true))).IsFailure); // someone else's account
    }

    [Fact]
    public async Task PastedProfilesLinkUnverifiedAndProvenOwnersTakeThemOver()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;

        var linked = (await sender.Send(new LinkSteamCommand(Kai, "https://steamcommunity.com/id/kai/"))).Value;
        Assert.Equal((KaiSteam, false), (linked.SteamId, linked.Verified));
        Assert.True((await sender.Send(new LinkSteamCommand(Kai, "not a profile!"))).IsFailure);

        // Someone else pasting the same profile is refused; proving it through Discord takes it over.
        Assert.Equal("Players.SteamTaken", (await sender.Send(new LinkSteamCommand(Mira, KaiSteam))).Error.Code);
        var proven = (await sender.Send(new LinkDiscordSteamCommand(Mira, KaiSteam, DiscordVerified: true))).Value;
        Assert.True(proven.Verified);
        Assert.Null((await sender.Send(new GetMyGamesQuery(Kai))).Value.Steam);

        // A verified account can't be taken over, even by Steam sign-in.
        fixture.Steam.SignedIn = KaiSteam;
        Assert.Equal("Players.SteamVerifiedElsewhere", (await sender.Send(new VerifySteamCommand(Kai, new Dictionary<string, string>(), "https://x"))).Error.Code);
        fixture.Steam.SignedIn = null;
        Assert.Equal("Players.SteamSignInFailed", (await sender.Send(new VerifySteamCommand(Kai, new Dictionary<string, string>(), "https://x"))).Error.Code);
    }

    [Fact]
    public async Task ImportKeepsMarksMatchesHandAddedGamesAndUnlinkRemovesOnlyImports()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Assert.Equal("Players.SteamNotLinked", (await sender.Send(new ImportSteamGamesCommand(Kai))).Error.Code);
        await sender.Send(new LinkSteamCommand(Kai, KaiSteam));

        fixture.Steam.Library = new SteamLibrary(SteamLibraryStatus.NotConfigured, []);
        Assert.Equal("Players.SteamImportOff", (await sender.Send(new ImportSteamGamesCommand(Kai))).Error.Code);
        fixture.Steam.Library = new SteamLibrary(SteamLibraryStatus.Private, []);
        Assert.Equal("Players.SteamPrivate", (await sender.Send(new ImportSteamGamesCommand(Kai))).Error.Code);

        await sender.Send(new AddPlayerGameCommand(Kai, "counter-strike 2", null, null, null, GamePreference.Want));
        await sender.Send(new AddPlayerGameCommand(Kai, "League of Legends", null, "igdb", "115", GamePreference.Want));
        fixture.Steam.Library = new SteamLibrary(SteamLibraryStatus.Ok,
            [new SteamOwnedGame("730", "Counter-Strike 2", 6000, 120), new SteamOwnedGame("570", "Dota 2", 30, 0)]);
        var import = (await sender.Send(new ImportSteamGamesCommand(Kai))).Value;
        Assert.Equal((1, 1, 2), (import.Added, import.Updated, import.Total));

        var games = (await sender.Send(new GetMyGamesQuery(Kai))).Value.Games;
        var cs2 = Assert.Single(games, game => game.Key == "steam:730");
        Assert.Equal((GamePreference.Want, 6000, "Counter-Strike 2"), (cs2.Preference, cs2.PlaytimeMinutes, cs2.Name));
        Assert.Equal(GamePreference.Can, games.Single(game => game.Key == "steam:570").Preference);
        Assert.DoesNotContain(games, game => game.Key.StartsWith("name:", StringComparison.Ordinal));

        // A second import doesn't duplicate anything and keeps the marks.
        await sender.Send(new SetGamePreferenceCommand(Kai, "steam:570", GamePreference.Wont));
        Assert.Equal((0, 2), ((await sender.Send(new ImportSteamGamesCommand(Kai))).Value.Added, (await sender.Send(new GetMyGamesQuery(Kai))).Value.Games.Count(game => game.Provider == "steam")));
        Assert.Equal(GamePreference.Wont, (await sender.Send(new GetMyGamesQuery(Kai))).Value.Games.Single(game => game.Key == "steam:570").Preference);

        await sender.Send(new UnlinkSteamCommand(Kai));
        var left = (await sender.Send(new GetMyGamesQuery(Kai))).Value;
        Assert.Null(left.Steam);
        // Games added by hand stay, including Counter-Strike 2, which the import matched and gave its Steam id.
        Assert.Equal(["igdb:115", "steam:730"], left.Games.Select(game => game.Key).Order());
    }

    [Fact]
    public async Task AddingAKnownGameChangesItsMarkAndBadInputIsRefused()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        var first = (await sender.Send(new AddPlayerGameCommand(Kai, "Deep Rock Galactic", "https://store.steampowered.com/app/548430/", null, null, GamePreference.Can))).Value;
        Assert.Equal("steam:548430", first.Key);
        var again = (await sender.Send(new AddPlayerGameCommand(Kai, "deep rock galactic", null, null, null, GamePreference.Want))).Value;
        Assert.Equal(("steam:548430", GamePreference.Want), (again.Key, again.Preference));
        Assert.Single((await sender.Send(new GetMyGamesQuery(Kai))).Value.Games);

        Assert.True((await sender.Send(new AddPlayerGameCommand(Kai, " ", null, null, null, GamePreference.Want))).IsFailure);
        Assert.True((await sender.Send(new AddPlayerGameCommand(Kai, "Tetris", null, null, null, (GamePreference)7))).IsFailure);
        Assert.True((await sender.Send(new AddPlayerGameCommand(Kai, "Tetris", "not a link", null, null, GamePreference.Want))).IsFailure);
        Assert.True((await sender.Send(new SetGamePreferenceCommand(Kai, "steam:1", GamePreference.Want))).IsFailure);
        Assert.True((await sender.Send(new RemovePlayerGameCommand(Kai, "steam:548430"))).IsSuccess);
        Assert.True((await sender.Send(new RemovePlayerGameCommand(Kai, "steam:548430"))).IsFailure);
    }

    [Fact]
    public async Task GroupsSeeTheirMembersMarksAndGamesSeveralMembersWant()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        var groups = fixture.Storage.GetRequiredService<IGuildGroupStore>();
        await groups.UpdateAsync(Guild, "Squad", _ => new GuildGroup("Squad", Kai, [Kai, Mira], []));
        var groupId = (await groups.ListAsync(Guild)).Single().Id;
        await groups.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), groupId, "Dota 2", GroupGame.SteamProvider, "570"));

        await sender.Send(new AddPlayerGameCommand(Kai, "Dota 2", "570", null, null, GamePreference.Want));
        await sender.Send(new AddPlayerGameCommand(Mira, "dota 2", null, null, null, GamePreference.Wont));
        await sender.Send(new AddPlayerGameCommand(Kai, "Helldivers 2", "553850", null, null, GamePreference.Want));
        await sender.Send(new AddPlayerGameCommand(Mira, "Helldivers 2", null, "steam", "553850", GamePreference.Want));
        await sender.Send(new AddPlayerGameCommand(Mira, "Celeste", null, null, null, GamePreference.Want));
        await sender.Send(new AddPlayerGameCommand(Taro, "Helldivers 2", "553850", null, null, GamePreference.Want)); // not in the group

        var interest = (await sender.Send(new GetGroupGameInterestQuery(new GroupActor(Guild, Kai, false), groupId))).Value;
        var dota = Assert.Single(interest.Games.Values);
        Assert.Equal([Kai], dota.Want);
        Assert.Equal([Mira], dota.Wont);
        var popular = Assert.Single(interest.Popular);
        Assert.Equal(("Helldivers 2", 2), (popular.Name, popular.Want.Count));

        Assert.True((await sender.Send(new GetGroupGameInterestQuery(new GroupActor(Guild, Taro, false), groupId))).IsFailure);
    }

    [Theory]
    [InlineData("76561198000000001", "76561198000000001", null)]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001/", "76561198000000001", null)]
    [InlineData("steamcommunity.com/id/kai_frags", null, "kai_frags")]
    [InlineData("kai_frags", null, "kai_frags")]
    [InlineData("https://example.com/id/kai", null, null)]
    [InlineData("12345", null, "12345")]
    [InlineData("a b", null, null)]
    public void PastedSteamProfilesAreRecognised(string input, string? steamId, string? vanity) =>
        Assert.Equal((steamId, vanity), SteamDirectory.Parse(input));

    [Fact]
    public void SteamLibrariesSignInsAndDiscordConnectionsAreRead()
    {
        using var library = JsonDocument.Parse("""{"response":{"game_count":1,"games":[{"appid":730,"name":"Counter-Strike 2","playtime_forever":61,"playtime_2weeks":5}]}}""");
        var parsed = SteamDirectory.ParseLibrary(library.RootElement);
        Assert.Equal(SteamLibraryStatus.Ok, parsed.Status);
        Assert.Equal(new SteamOwnedGame("730", "Counter-Strike 2", 61, 5), Assert.Single(parsed.Games));
        using var closed = JsonDocument.Parse("""{"response":{}}""");
        Assert.Equal(SteamLibraryStatus.Private, SteamDirectory.ParseLibrary(closed.RootElement).Status);

        Assert.Equal(KaiSteam, SteamDirectory.ClaimedId("https://steamcommunity.com/openid/id/" + KaiSteam));
        Assert.Null(SteamDirectory.ClaimedId("https://evil.example/openid/id/" + KaiSteam));
        Assert.StartsWith("https://steamcommunity.com/openid/login?openid.ns=", SteamIds.SignInUrl("https://dash/games/1/steam/return?state=a", "https://dash/"),
            StringComparison.Ordinal);

        using var connections = JsonDocument.Parse($$"""[{"type":"twitch","id":"x","name":"kai"},{"type":"steam","id":"{{KaiSteam}}","name":"Kai","verified":true}]""");
        Assert.Equal((KaiSteam, "Kai", true), DiscordConnections.Steam(connections.RootElement));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public FakeSteam Steam { get; } = new();
        public ISender Sender => application.GetRequiredService<ISender>();
        public ServiceProvider Storage => database.Services;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton(storage.GetRequiredService<IPlayerGameStore>())
                .AddSingleton(storage.GetRequiredService<IClock>())
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

    internal sealed class FakeSteam : ISteamDirectory
    {
        public SteamLibrary Library { get; set; } = new(SteamLibraryStatus.Ok, []);
        public string? SignedIn { get; set; }
        public bool CanImport => Library.Status != SteamLibraryStatus.NotConfigured;

        public Task<string?> ResolveAsync(string input, CancellationToken cancellationToken) => Task.FromResult(input.Trim() switch
        {
            "https://steamcommunity.com/id/kai/" => KaiSteam,
            KaiSteam => KaiSteam,
            MiraSteam => MiraSteam,
            TwinkSteam => TwinkSteam,
            _ => (string?)null
        });

        /// <summary>Libraries of particular accounts (twinks); any other account has <see cref="Library"/>.</summary>
        public Dictionary<string, SteamLibrary> Libraries { get; } = new(StringComparer.Ordinal);

        public Task<SteamLibrary> GetOwnedGamesAsync(string steamId, CancellationToken cancellationToken) =>
            Task.FromResult(Libraries.GetValueOrDefault(steamId) ?? Library);

        public Task<string?> VerifySignInAsync(IReadOnlyDictionary<string, string> response, string returnTo, CancellationToken cancellationToken) =>
            Task.FromResult(SignedIn);

        public Dictionary<string, string> Playing { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, string>> GetPlayingAsync(IReadOnlyCollection<string> steamIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Playing.Where(pair => steamIds.Contains(pair.Key)).ToDictionary());
    }
}
