using Discord;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.Polls;
using HappyMix.Application.Abstractions.Watch;
using HappyMix.Application.Guilds;
using HappyMix.Application.Polls;
using HappyMix.Application.Watch;
using HappyMix.Bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Poll = HappyMix.Application.Abstractions.Polls.Poll;

namespace HappyMix.Application.Tests.Polls;

/// <summary>An offline catalog: one series (2 seasons of 3 and 2 episodes), one film and one game.</summary>
internal sealed class FakeCatalog : IMediaCatalog
{
    public static readonly MediaInfo Series = new("series", "tvmaze", "82", "Arcane", "https://www.tvmaze.com/shows/82/arcane", "https://img.test/arcane.jpg",
        2021, "Two sisters.", ["Drama", "Animation"], 9.0, "Ended", 40, [new MediaSeason(1, 3), new MediaSeason(2, 2)]);
    public static readonly MediaInfo Film = new("film", "tmdb", "693134", "Dune: Part Two", "https://www.themoviedb.org/movie/693134", null,
        2024, null, ["Science Fiction"], 8.2, "Released", 166, []);
    public static readonly MediaInfo Game = new("game", "steam", "548430", "Deep Rock Galactic", "https://store.steampowered.com/app/548430", null,
        2020, null, ["Action"], 8.5, null, null, []);
    private static readonly MediaInfo[] All = [Series, Film, Game];

    public bool SupportsFilms => true;
    public Task<IReadOnlyList<MediaInfo>> SearchAsync(string kind, string query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaInfo>>(All.Where(item => item.Kind == kind && item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
    public Task<MediaInfo?> LookupAsync(string url, CancellationToken cancellationToken) => Task.FromResult(url switch
    {
        _ when url.Contains("imdb.com/title/tt11126994", StringComparison.Ordinal) => Series,
        _ when url.Contains("store.steampowered.com/app/548430", StringComparison.Ordinal) => Game,
        _ => (MediaInfo?)null
    });
    public Task<MediaInfo?> GetAsync(MediaRef reference, CancellationToken cancellationToken) =>
        Task.FromResult(All.FirstOrDefault(item => item.Ref == reference));
}

public sealed class VotesAndWatchlistTests
{
    private const ulong Guild = PollTests.Guild, Organizer = PollTests.Organizer, Kai = PollTests.Kai;
    private static GroupActor Actor(ulong user) => new(Guild, user, false);

    [Fact]
    public async Task BareCatalogLinksBecomeOptionsWithDetails()
    {
        using var fixture = await PollTests.Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        await fixture.Sender.Send(new SetGroupTagsCommand(Actor(Organizer), group, ["series"]));
        var id = (await fixture.Sender.Send(new CreatePollCommand(Actor(Kai), group, "Which series?",
            ["https://www.imdb.com/title/tt11126994/", "Something else"], false, TimeSpan.FromHours(1), [], false, Guid.NewGuid(), 555))).Value;

        var poll = (await fixture.Sender.Send(new GetPollQuery(Actor(Kai), id))).Value;
        Assert.Equal(555UL, poll.ChannelId);
        var arcane = poll.Options[0];
        Assert.Equal(("Arcane", "series", FakeCatalog.Series.Ref), (arcane.Label, arcane.Kind, arcane.Media));
        var board = (await fixture.Sender.Send(new GetPollBoardQuery(Actor(Kai), group))).Value;
        Assert.Equal("Arcane", board.Media[FakeCatalog.Series.Ref].Title);
        Assert.Equal([id], (await fixture.Database.GetRequiredService<IPollStore>().ListDiscordWorkAsync(fixture.Clock.UtcNow)).Select(item => item.Id));
    }

    [Fact]
    public async Task WatchlistTracksEpisodesAcrossSeasonsAndWhereTheGroupStopped()
    {
        using var fixture = await PollTests.Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        Assert.True((await fixture.Sender.Send(new AddWatchItemCommand(Actor(Kai), group, "series", FakeCatalog.Series.Ref, null, null))).IsFailure);
        var item = (await fixture.Sender.Send(new AddWatchItemCommand(Actor(Organizer), group, "series", FakeCatalog.Series.Ref, null, null))).Value;
        Assert.Equal(item.Id, (await fixture.Sender.Send(new AddWatchItemCommand(Actor(Organizer), group, "series", null, "https://www.imdb.com/title/tt11126994", null))).Value.Id);
        Assert.Equal(("Arcane", WatchStatus.Planned, 1, 1), (item.Title, item.Status, item.Season, item.Episode));

        item = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, Position: "23:40"))).Value;
        Assert.Equal((WatchStatus.Watching, 1420), (item.Status, item.PositionSeconds));
        Assert.True((await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, Position: "23:99"))).IsFailure);

        for (int index = 0; index < 3; index++)
            item = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, NextEpisode: true))).Value;
        Assert.Equal((2, 1, (int?)null), (item.Season, item.Episode, item.PositionSeconds));
        var entry = Assert.Single((await fixture.Sender.Send(new GetWatchlistQuery(Actor(Kai), group))).Value.Entries);
        Assert.Equal((3, 2), (entry.EpisodesSeen, entry.SeasonEpisodes));
        Assert.False((await fixture.Sender.Send(new GetWatchlistQuery(Actor(Kai), group))).Value.CanEdit);

        item = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, NextEpisode: true))).Value;
        item = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, NextEpisode: true))).Value;
        Assert.Equal(WatchStatus.Watched, item.Status);
        Assert.NotNull(item.FinishedAt);

        var film = (await fixture.Sender.Send(new AddWatchItemCommand(Actor(Organizer), group, "film", null, null, "Home movie"))).Value;
        Assert.Null(film.Source);
        film = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, film.Id, Position: "1:05:00"))).Value;
        Assert.Equal(3900, film.PositionSeconds);
        Assert.Equal(WatchStatus.Watched, (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, film.Id, NextEpisode: true))).Value.Status);
        Assert.True((await fixture.Sender.Send(new RemoveWatchItemCommand(Actor(Organizer), group, film.Id))).Value);
    }

    [Fact]
    public async Task PlannedWatchlistTitlesAreSuggestedForVotes()
    {
        using var fixture = await PollTests.Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        await fixture.Sender.Send(new SetGroupTagsCommand(Actor(Organizer), group, ["series"]));
        await fixture.Sender.Send(new AddWatchItemCommand(Actor(Organizer), group, "series", FakeCatalog.Series.Ref, null, null));
        var suggestion = Assert.Single((await fixture.Sender.Send(new GetPollBoardQuery(Actor(Kai), group))).Value.Suggestions);
        Assert.Equal(("Arcane", "watchlist"), (suggestion.Label, suggestion.Source));
    }

    [Theory]
    [InlineData("12:30", 750)]
    [InlineData("1:05:00", 3900)]
    [InlineData("45", 2700)]
    [InlineData("12:75", null)]
    [InlineData("soon", null)]
    public void PositionsAreReadAsTimes(string text, int? seconds) => Assert.Equal(seconds, WatchKinds.ParsePosition(text));

    [Fact]
    public void PresetsShowWhatMattersForEachKind()
    {
        Assert.Equal("2021 · 2 seasons, 5 episodes · Drama, Animation · ★ 9.0 · Ended", PollMessages.Details(FakeCatalog.Series, "en"));
        Assert.Equal("2024 · 166 min · Science Fiction · ★ 8.2", PollMessages.Details(FakeCatalog.Film, "en"));
        Assert.Equal("2020 · Action · ★ 8.5", PollMessages.Details(FakeCatalog.Game, "en"));
        Assert.StartsWith("2024 · 166 хв", PollMessages.Details(FakeCatalog.Film, "uk"), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordMessagesShowCardsWhileOpenAndTheWinnerWhenEnded()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var poll = new Poll(Guid.NewGuid(), Guild, null, Kai, "Movie night?", false, true, now.AddHours(-1), now.AddHours(1), null,
            [new PollOption(0, "Arcane", "series", FakeCatalog.Series.Url, FakeCatalog.Series.Ref), new PollOption(1, "Pizza first", "custom", null)],
            [], [new PollVote(Kai, 0), new PollVote(Organizer, 0)]);
        var media = new Dictionary<MediaRef, MediaInfo> { [FakeCatalog.Series.Ref] = FakeCatalog.Series };
        var tracked = new WatchItem(Guid.NewGuid(), Guild, Guid.NewGuid(), "series", "Arcane", null, null, "tvmaze", "82", WatchStatus.Watching, 2, 1, 754,
            Kai, now, now, null);
        var progress = new Dictionary<MediaRef, WatchItem> { [FakeCatalog.Series.Ref] = tracked };

        var (_, embeds, components) = PollMessages.Build(poll, media, progress, "en", now, "https://dash.test/votes/1#votes");
        Assert.Equal(2, embeds.Length);
        Assert.Equal("🗳️ Movie night?", embeds[0].Title);
        Assert.Contains("2 votes", embeds[0].Description, StringComparison.Ordinal);
        Assert.Equal("https://img.test/arcane.jpg", embeds[1].Thumbnail!.Value.Url);
        Assert.Contains("next: S2 E1 · stopped at 12:34", embeds[1].Description, StringComparison.Ordinal);
        var menu = components.Components.OfType<ActionRowComponent>().First().Components.OfType<SelectMenuComponent>().Single();
        Assert.Equal((1, 1, 2), (menu.MinValues, menu.MaxValues, menu.Options.Count));

        var (_, ended, final) = PollMessages.Build(poll with { ClosedAt = now }, media, progress, "en", now, null);
        Assert.StartsWith("🏁", ended[0].Title, StringComparison.Ordinal);
        Assert.Contains("**Arcane** won with 2 of 2 votes", ended[0].Description, StringComparison.Ordinal);
        Assert.Empty(final.Components);
    }
}
