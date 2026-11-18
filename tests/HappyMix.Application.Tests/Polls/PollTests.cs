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
    private const ulong Guild = 1, Organizer = 10, Kai = 20, Mira = 30, Outsider = 99;
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
    public async Task GroupVotesUseTagSuggestionsAndRememberWinners()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        await fixture.Database.GetRequiredService<IGuildGroupStore>().SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), group, "Deep Rock Galactic", "steam", "548430"));

        Assert.True((await fixture.Sender.Send(new SetGroupTagsCommand(Actor(Kai), group, ["games"]))).IsFailure);
        Assert.Equal(["games", "coop"], (await fixture.Sender.Send(new SetGroupTagsCommand(Actor(Organizer), group, ["games", "Coop"]))).Value);

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

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public MutableClock Clock { get; } = new();
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
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Events.IEventStore>())
                .AddSingleton<IClock>(fixture.Clock)
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

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
