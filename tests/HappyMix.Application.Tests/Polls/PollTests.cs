using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Polls;
using HappyMix.Application.Guilds;
using HappyMix.Application.Polls;
using HappyMix.Application.Tests.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Polls;

public sealed class PollTests
{
    internal const ulong Guild = 1, Organizer = 10, Kai = 20, Mira = 30, Outsider = 99;
    private static GroupActor Actor(ulong user, bool moderate = false) => new(Guild, user, moderate);

    [Theory]
    [InlineData("https://store.steampowered.com/app/730/CounterStrike_2/", "custom", "CounterStrike 2", "game")]
    [InlineData("https://myanimelist.net/anime/5114/Fullmetal_Alchemist__Brotherhood", "custom", "Fullmetal Alchemist Brotherhood", "anime")]
    [InlineData("Arcane https://www.imdb.com/title/tt11126994/", "game", "Arcane", "series")]
    [InlineData("Dune: Part Two https://www.imdb.com/title/tt15239678", "film", "Dune: Part Two", "film")]
    [InlineData("Lethal Company", "game", "Lethal Company", "game")]
    public void OptionsReadTitlesAndKindsFromLinks(string text, string fallback, string label, string kind)
    {
        var (option, problem) = PollLinks.Parse(text, fallback);
        Assert.Null(problem);
        Assert.Equal((label, kind), (option!.Label, option.Kind));
    }

    [Theory]
    [InlineData("https://www.imdb.com/title/tt11126994/", "Polls_LinkNeedsTitle")]
    [InlineData("   ", "Polls_EmptyOption")]
    public void OptionsWithoutATitleAreRejected(string text, string problem) => Assert.Equal(problem, PollLinks.Parse(text, "custom").Problem);

    [Fact]
    public void TagsAreCleanedAndLimited()
    {
        Assert.Equal(["games", "anime", "ranked"], GroupTags.Normalize(["Games", " #anime", "games", "", "ranked"])!);
        Assert.Null(GroupTags.Normalize(Enumerable.Range(0, 9).Select(index => "t" + index)));
        Assert.Null(GroupTags.Normalize([new string('x', 25)]));
    }

    [Fact]
    public async Task MembersWantedGamesLeadTheVoteSuggestions()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        var games = fixture.Database.GetRequiredService<IGuildGroupStore>();
        await games.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), group, "Deep Rock Galactic", "steam", "548430"));
        await games.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), group, "Dota 2", "steam", "570"));
        await fixture.Sender.Send(new SetGroupTagsCommand(Actor(Organizer), group, ["games"]));
        foreach (ulong member in new[] { Organizer, Kai })
        {
            await fixture.Sender.Send(new HappyMix.Application.Players.AddPlayerGameCommand(member, "Dota 2", "570", null, null, HappyMix.Application.Abstractions.Players.GamePreference.Want));
            await fixture.Sender.Send(new HappyMix.Application.Players.AddPlayerGameCommand(member, "Helldivers 2", "553850", null, null, HappyMix.Application.Abstractions.Players.GamePreference.Want));
        }

        var suggestions = (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Kai), group))).Value.Suggestions;
        Assert.Equal(2, suggestions.Single(item => item.Label == "Dota 2").Wanted);
        var wanted = suggestions.Single(item => item.Label == "Helldivers 2");
        Assert.Equal(("wanted", 2, "https://store.steampowered.com/app/553850"), (wanted.Source, wanted.Wanted, wanted.Url));
        Assert.Equal("Deep Rock Galactic", suggestions[^1].Label); // nobody wants it, so it comes last
    }

    [Fact]
    public async Task BuyVotesCountSteamOwnersAndCatchUpAfterAnImport()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai, Mira]);
        await fixture.Database.GetRequiredService<IGuildGroupStore>().SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), group, "Deep Rock Galactic", "steam", "548430"));
        var players = fixture.Database.GetRequiredService<HappyMix.Application.Abstractions.Players.IPlayerGameStore>();
        HappyMix.Application.Abstractions.Players.PlayerGame Game(ulong user, bool fromSteam) => new(user, "steam:548430", "Deep Rock Galactic", "steam", "548430",
            HappyMix.Application.Abstractions.Players.GamePreference.Can, fromSteam, fromSteam ? 600 : null, fromSteam ? 0 : null, fixture.Clock.UtcNow);
        await players.SaveAsync(Game(Kai, fromSteam: true));
        await players.SaveAsync(Game(Organizer, fromSteam: false)); // marked by hand: not proof of owning it

        Assert.True((await fixture.Sender.Send(new CreateBuyVoteCommand(Actor(Kai), group, "  ", "Will you buy {0}?", TimeSpan.FromDays(1), Guid.NewGuid()))).IsFailure);
        Assert.True((await fixture.Sender.Send(new CreateBuyVoteCommand(Actor(Outsider), group, "Deep Rock Galactic", "Will you buy {0}?", TimeSpan.FromDays(1), Guid.NewGuid()))).IsFailure);
        var id = (await fixture.Sender.Send(new CreateBuyVoteCommand(Actor(Organizer), group, "deep rock galactic", "Will you buy {0}?", TimeSpan.FromDays(3), Guid.NewGuid()))).Value;

        var poll = (await fixture.Sender.Send(new GetPollQuery(Actor(Mira), id))).Value;
        Assert.Equal((true, "steam:548430", "Deep Rock Galactic", "Will you buy Deep Rock Galactic?"), (poll.IsBuy, poll.GameKey, poll.GameName, poll.Question));
        Assert.Equal(BuyVote.Labels, poll.Options.Select(option => option.Label));
        Assert.Equal([new PollVote(Kai, BuyVote.Have)], poll.Votes);

        // Mira says she's ready to buy, buys it, and her next Steam import counts her as an owner.
        await fixture.Sender.Send(new VotePollCommand(Actor(Mira), id, [BuyVote.Ready]));
        Assert.Empty((await fixture.Sender.Send(new SyncBuyVotesCommand(Mira))).Value);
        await players.SaveAsync(Game(Mira, fromSteam: true));
        Assert.Equal([(Guild, id)], (await fixture.Sender.Send(new SyncBuyVotesCommand(Mira))).Value);
        Assert.Empty((await fixture.Sender.Send(new SyncBuyVotesCommand(Mira))).Value);
        poll = (await fixture.Sender.Send(new GetPollQuery(Actor(Mira), id))).Value;
        Assert.Equal((2, 0), (poll.Count(BuyVote.Have), poll.Count(BuyVote.Ready)));

        // Ended votes are left alone.
        fixture.Clock.Advance(TimeSpan.FromDays(4));
        await players.SaveAsync(Game(Organizer, fromSteam: true));
        Assert.Empty((await fixture.Sender.Send(new SyncBuyVotesCommand(Organizer))).Value);
    }

    [Fact]
    public async Task BuyVotesAreAVoteTypeForTheServerAndChosenPeopleToo()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        await fixture.Database.GetRequiredService<IGuildGroupStore>().SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), group, "Deep Rock Galactic", "steam", "548430"));
        var players = fixture.Database.GetRequiredService<HappyMix.Application.Abstractions.Players.IPlayerGameStore>();
        HappyMix.Application.Abstractions.Players.PlayerGame Owned(ulong user) => new(user, "steam:548430", "Deep Rock Galactic", "steam", "548430",
            HappyMix.Application.Abstractions.Players.GamePreference.Can, true, 600, 0, fixture.Clock.UtcNow);
        await players.SaveAsync(Owned(Kai));
        await players.SaveAsync(Owned(Outsider)); // owns it, but isn't in the server
        fixture.ServerMembers.UnionWith([Organizer, Kai, Mira]);

        // Server votes offer the server's group games.
        var board = (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Mira), null))).Value;
        Assert.Contains(board.BuyGames, item => item.Label == "Deep Rock Galactic" && item.Url == "https://store.steampowered.com/app/548430");

        // Chosen people: the asker and whoever was picked; owners among them are counted at once.
        Assert.True((await fixture.Sender.Send(new CreateBuyVoteCommand(Actor(Organizer), null, "Deep Rock Galactic", "Will you buy {0}?",
            TimeSpan.FromDays(1), Guid.NewGuid()))).IsFailure);
        var chosen = (await fixture.Sender.Send(new CreateBuyVoteCommand(Actor(Organizer), null, "Deep Rock Galactic", "Will you buy {0}?",
            TimeSpan.FromDays(1), Guid.NewGuid(), Audience: [Kai]))).Value;
        var poll = (await fixture.Sender.Send(new GetPollQuery(Actor(Kai), chosen))).Value;
        Assert.Equal((true, (Guid?)null, false), (poll.IsBuy, poll.GroupId, poll.Everyone));
        Assert.Equal([Organizer, Kai], poll.Audience.Order());
        Assert.Equal([new PollVote(Kai, BuyVote.Have)], poll.Votes);
        Assert.True((await fixture.Sender.Send(new GetPollQuery(Actor(Mira), chosen))).IsFailure);

        // Everyone in the server: owners who are in the server are counted, others aren't.
        var everyone = (await fixture.Sender.Send(new CreateBuyVoteCommand(Actor(Organizer), null, "https://store.steampowered.com/app/548430/",
            "Will you buy {0}?", TimeSpan.FromDays(1), Guid.NewGuid(), Everyone: true))).Value;
        poll = (await fixture.Sender.Send(new GetPollQuery(Actor(Mira), everyone))).Value;
        Assert.Equal((true, "Deep Rock Galactic"), (poll.Everyone, poll.GameName));
        Assert.Equal([new PollVote(Kai, BuyVote.Have)], poll.Votes);

        // Mira buys it: her import counts her in the server-wide vote, not in the one she wasn't chosen for.
        await players.SaveAsync(Owned(Mira));
        Assert.Equal([(Guild, everyone)], (await fixture.Sender.Send(new SyncBuyVotesCommand(Mira))).Value);
        await players.SaveAsync(Owned(Outsider) with { Name = "Deep Rock Galactic" });
        Assert.Empty((await fixture.Sender.Send(new SyncBuyVotesCommand(Outsider))).Value);
        board = (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Organizer), null))).Value;
        Assert.Equal(2, board.Open.Count);
    }

    [Fact]
    public void BuyVoteGamesComeFromLinksGroupGamesOrNames()
    {
        GroupGame[] games = [new(Guid.NewGuid(), Guid.NewGuid(), "Dota 2", "steam", "570")];
        Assert.Equal(("steam:1145360", "Hades"), BuyVoteHandlers.Resolve("https://store.steampowered.com/app/1145360/Hades/", games, []));
        Assert.Equal(("steam:570", "Dota 2"), BuyVoteHandlers.Resolve("DOTA 2", games, []));
        Assert.Equal(("steam:570", "Dota 2"), BuyVoteHandlers.Resolve("steam:570", games, []));
        Assert.Equal(("name:some indie", "Some Indie"), BuyVoteHandlers.Resolve("Some Indie", games, []));
        Assert.Null(BuyVoteHandlers.Resolve(" ", games, []));
    }

    [Fact]
    public async Task GroupVotesUseTagSuggestionsAndRememberWinners()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        await fixture.Database.GetRequiredService<IGuildGroupStore>().SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), group, "Deep Rock Galactic", "steam", "548430"));

        Assert.True((await fixture.Sender.Send(new SetGroupTagsCommand(Actor(Kai), group, ["games"]))).IsFailure);
        Assert.Equal(["games", "coop"], (await fixture.Sender.Send(new SetGroupTagsCommand(Actor(Organizer), group, ["games", "Coop"]))).Value);
        Assert.Equal(["games", "coop"], (await fixture.Sender.Send(new GetGroupTagsQuery(Guild))).Value[group]);

        var board = (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Kai), group))).Value;
        Assert.Equal("Polls_Question_games", board.DefaultQuestion);
        var game = Assert.Single(board.Suggestions);
        Assert.Equal(("Deep Rock Galactic", "https://store.steampowered.com/app/548430"), (game.Label, game.Url));
        Assert.True((await fixture.Sender.Send(new GetPollBoardQuery(Actor(Outsider), group))).IsFailure);

        var id = (await fixture.Sender.Send(new CreatePollCommand(Actor(Kai), group, "What are we playing?",
            ["Deep Rock Galactic https://store.steampowered.com/app/548430", "https://store.steampowered.com/app/1966720/Lethal_Company/", "deep rock galactic"],
            false, TimeSpan.FromHours(3), [], false, Guid.NewGuid()))).Value;
        var poll = (await fixture.Database.GetRequiredService<IPollStore>().GetAsync(Guild, id))!;
        Assert.Equal(["Deep Rock Galactic", "Lethal Company"], poll.Options.Select(option => option.Label));

        Assert.True((await fixture.Sender.Send(new VotePollCommand(Actor(Outsider), id, [0]))).IsFailure);
        Assert.True((await fixture.Sender.Send(new VotePollCommand(Actor(Kai), id, [0, 1]))).IsFailure);
        await fixture.Sender.Send(new VotePollCommand(Actor(Kai), id, [1]));
        await fixture.Sender.Send(new VotePollCommand(Actor(Organizer), id, [1]));
        Assert.True((await fixture.Sender.Send(new ClosePollCommand(Actor(Organizer), id))).IsSuccess);
        Assert.True((await fixture.Sender.Send(new VotePollCommand(Actor(Kai), id, [0]))).IsFailure);

        board = (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Kai), group))).Value;
        Assert.Empty(board.Open);
        Assert.Equal("Lethal Company", Assert.Single(board.Ended).Leader!.Label);
        // The winner is remembered and goes to the end, so the next vote leads with something else.
        Assert.Equal(["Deep Rock Galactic", "Lethal Company"], board.Suggestions.Select(item => item.Label));
        Assert.Equal(1, board.Suggestions[1].TimesWon);
    }

    [Fact]
    public async Task ServerVotesNeedNoGroupAndOnlyTheirAudienceSeesThem()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Sender.Send(new CreatePollCommand(Actor(Kai), null, "Movie night?", ["Yes", "No"], false,
            TimeSpan.FromHours(1), [], false, Guid.NewGuid()))).IsFailure);
        var chosen = (await fixture.Sender.Send(new CreatePollCommand(Actor(Kai), null, "Movie night?", ["Yes", "No"], false,
            TimeSpan.FromHours(1), [Mira], false, Guid.NewGuid()))).Value;
        var everyone = (await fixture.Sender.Send(new CreatePollCommand(Actor(Kai), null, "Server event?", ["Friday", "Saturday"], true,
            TimeSpan.FromDays(1), [], true, Guid.NewGuid()))).Value;

        Assert.Equal(new[] { chosen, everyone }.Order(),
            (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Mira), null))).Value.Open.Select(poll => poll.Id).Order());
        Assert.Equal([everyone], (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Outsider), null))).Value.Open.Select(poll => poll.Id));
        Assert.True((await fixture.Sender.Send(new VotePollCommand(Actor(Outsider), chosen, [0]))).IsFailure);
        Assert.Equal(2, (await fixture.Sender.Send(new VotePollCommand(Actor(Outsider), everyone, [0, 1]))).Value.Votes.Count);
        Assert.True((await fixture.Sender.Send(new ClosePollCommand(Actor(Mira), chosen))).IsFailure);

        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var board = (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Mira), null))).Value;
        Assert.Equal([chosen], board.Ended.Select(poll => poll.Id));
        Assert.Null(board.Ended[0].Leader);
    }

    internal sealed class NoSteam : HappyMix.Application.Abstractions.Players.ISteamDirectory
    {
        public bool CanImport => false;
        public Task<string?> ResolveAsync(string input, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<HappyMix.Application.Abstractions.Players.SteamLibrary> GetOwnedGamesAsync(string steamId, CancellationToken cancellationToken) =>
            Task.FromResult(new HappyMix.Application.Abstractions.Players.SteamLibrary(HappyMix.Application.Abstractions.Players.SteamLibraryStatus.NotConfigured, []));
        public Task<string?> VerifySignInAsync(IReadOnlyDictionary<string, string> response, string returnTo, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<string, string>> GetPlayingAsync(IReadOnlyCollection<string> steamIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    internal sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public MutableClock Clock { get; } = new();
        /// <summary>Who Discord says is in the server.</summary>
        public HashSet<ulong> ServerMembers { get; } = [];
        public ISender Sender => application.GetRequiredService<ISender>();
        public IServiceProvider Database => database.Services;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton(storage.GetRequiredService<IPollStore>())
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Watch.IWatchStore>())
                .AddSingleton<HappyMix.Application.Abstractions.Media.IMediaCatalog>(new FakeCatalog())
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Events.IEventStore>())
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Players.IPlayerGameStore>())
                .AddSingleton<HappyMix.Application.Abstractions.Players.ISteamDirectory>(new NoSteam())
                .AddSingleton<IGuildMemberGateway>(new InServer(fixture.ServerMembers))
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        public async Task<Guid> GroupAsync(ulong organizer, ulong[] members)
        {
            var store = database.Services.GetRequiredService<IGuildGroupStore>();
            await store.UpdateAsync(Guild, "Squad", _ => new GuildGroup("Squad", organizer, [organizer, .. members], []));
            return (await store.ListAsync(Guild)).Single().Id;
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }

    private sealed class InServer(HashSet<ulong> members) : IGuildMemberGateway
    {
        public Task<bool> IsAdministratorAsync(ulong guildId, ulong userId) => Task.FromResult(false);
        public Task<bool> IsMemberAsync(ulong guildId, ulong userId) => Task.FromResult(guildId == Guild && members.Contains(userId));
    }

    internal sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
