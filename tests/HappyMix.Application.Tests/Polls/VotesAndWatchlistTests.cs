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
    public async Task SeasonsAndEpisodesMustExistAndFinishingMarksTheWholeSeries()
    {
        using var fixture = await PollTests.Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai]);
        var item = (await fixture.Sender.Send(new AddWatchItemCommand(Actor(Organizer), group, "series", FakeCatalog.Series.Ref, null, null))).Value;

        // Arcane in the fake catalog: season 1 has 3 episodes, season 2 has 2, and there is no season 3.
        Assert.True((await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, Season: 3))).IsFailure);
        Assert.True((await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, Season: 1, Episode: 4))).IsFailure);
        item = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, Season: 1, Episode: 3, Position: "10:00"))).Value;
        Assert.Equal((1, 3, (int?)600), (item.Season, item.Episode, item.PositionSeconds));
        item = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, Season: 2))).Value;
        Assert.Equal((2, 1), (item.Season, item.Episode));

        item = (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, item.Id, Finish: true))).Value;
        Assert.Equal((WatchStatus.Watched, 2, 2, (int?)null), (item.Status, item.Season, item.Episode, item.PositionSeconds));
        Assert.NotNull(item.FinishedAt);
        var entry = Assert.Single((await fixture.Sender.Send(new GetWatchlistQuery(Actor(Kai), group))).Value.Entries);
        Assert.Equal(5, entry.EpisodesSeen);
        Assert.Equal([1, 2], entry.Seasons.Select(season => season.Number));

        // Titles the catalog doesn't know keep free numbers.
        var home = (await fixture.Sender.Send(new AddWatchItemCommand(Actor(Organizer), group, "series", null, null, "Home videos"))).Value;
        Assert.Equal(7, (await fixture.Sender.Send(new UpdateWatchItemCommand(Actor(Organizer), group, home.Id, Season: 7))).Value.Season);
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

    [Fact]
    public async Task DiscordPollVotesAreCopiedAndTheDashboardSendsVotersToDiscord()
    {
        using var fixture = await PollTests.Fixture.CreateAsync();
        var id = (await fixture.Sender.Send(new CreatePollCommand(Actor(Kai), null, "Game night?", ["Friday", "Saturday"], false,
            TimeSpan.FromHours(3), [], true, Guid.NewGuid(), 555))).Value;
        var store = fixture.Database.GetRequiredService<IPollStore>();
        await store.SetMessageAsync(id, 555, 777, false, native: true);

        Assert.True((await fixture.Sender.Send(new CopyDiscordPollVoteCommand(Guild, Kai, 777, 1, true))).Value);
        Assert.True((await fixture.Sender.Send(new CopyDiscordPollVoteCommand(Guild, Organizer, 777, 0, true))).Value);
        Assert.False((await fixture.Sender.Send(new CopyDiscordPollVoteCommand(Guild, Organizer, 777, 9, true))).Value);
        Assert.False((await fixture.Sender.Send(new CopyDiscordPollVoteCommand(Guild + 1, Organizer, 777, 0, true))).Value);
        await fixture.Sender.Send(new CopyDiscordPollVoteCommand(Guild, Organizer, 777, 0, false));
        var poll = (await store.FindByMessageAsync(777))!;
        Assert.True(poll.Native);
        Assert.Equal([new PollVote(Kai, 1)], poll.Votes);

        Assert.True((await fixture.Sender.Send(new VotePollCommand(Actor(Kai), id, [0]))).IsFailure);

        await fixture.Sender.Send(new SyncDiscordPollVotesCommand(Guild, id, new Dictionary<ulong, int[]> { [Kai] = [0], [Organizer] = [1] }));
        Assert.Equal([(Organizer, 1), (Kai, 0)], (await store.GetAsync(Guild, id))!.Votes.Select(vote => (vote.UserId, vote.Position)).Order());
        Assert.Equal([id], (await store.ListOpenNativeAsync(fixture.Clock.UtcNow)).Select(item => item.Id));
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

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static Poll Sample(bool everyone = true, IReadOnlyList<PollOption>? options = null, IReadOnlyList<ulong>? audience = null) =>
        new(Guid.NewGuid(), Guild, null, Kai, "Movie night?", false, everyone, Now.AddHours(-1), Now.AddHours(2).AddMinutes(30), null,
            options ?? [new PollOption(0, "Arcane", "series", FakeCatalog.Series.Url, FakeCatalog.Series.Ref), new PollOption(1, "Pizza first", "custom", null)],
            audience ?? [], [new PollVote(Kai, 0), new PollVote(Organizer, 0)]);

    [Fact]
    public void EveryVoteBecomesADiscordPollAndChosenMemberVotesArePrivate()
    {
        var plain = Sample(options: [new PollOption(0, "Friday", "custom", null), new PollOption(1, new string('x', 70), "series", null)]);
        Assert.False(PollMessages.IsPrivate(plain));
        Assert.True(PollMessages.IsPrivate(plain with { Everyone = false, Audience = [Kai, Organizer] }));
        Assert.False(PollMessages.IsPrivate(plain with { Everyone = false, GroupId = Guid.NewGuid() }));

        var native = PollMessages.NativePoll(plain with { Multiple = true }, Now);
        Assert.Equal("Movie night?", native.Question.Text);
        Assert.Equal("Friday", native.Answers[0].Text);
        Assert.Equal(55, native.Answers[1].Text.Length);
        Assert.EndsWith("…", native.Answers[1].Text, StringComparison.Ordinal);
        Assert.NotNull(native.Answers[1].Emoji);
        Assert.Equal(3u, native.Duration);
        Assert.True(native.AllowMultiselect);
    }

    [Fact]
    public void TheInfoCardAboveAPollHasNoVoteControls()
    {
        var media = new Dictionary<MediaRef, MediaInfo> { [FakeCatalog.Series.Ref] = FakeCatalog.Series };
        var card = PollMessages.BuildCards(Sample(), media, new Dictionary<MediaRef, WatchItem>(), "en", Now, "https://dash.test/votes/1#votes", interactive: false);
        var all = Flatten(card.Components).ToArray();
        Assert.Empty(all.OfType<SelectMenuComponent>());
        Assert.Single(all.OfType<SectionComponent>());
        Assert.DoesNotContain(all.OfType<TextDisplayComponent>(), item => item.Content.Contains('█'));
        Assert.Contains(all.OfType<TextDisplayComponent>(), item => item.Content.Contains("Vote in the poll below", StringComparison.Ordinal));
        Assert.Single(all.OfType<ButtonComponent>());
    }

    [Fact]
    public void TitleVotesBecomeCardsWithPostersAndStayWithinDiscordLimits()
    {
        var poll = Sample();
        var media = new Dictionary<MediaRef, MediaInfo> { [FakeCatalog.Series.Ref] = FakeCatalog.Series };
        var tracked = new WatchItem(Guid.NewGuid(), Guild, Guid.NewGuid(), "series", "Arcane", null, null, "tvmaze", "82", WatchStatus.Watching, 2, 1, 754,
            Kai, Now, Now, null);
        var progress = new Dictionary<MediaRef, WatchItem> { [FakeCatalog.Series.Ref] = tracked };

        var open = PollMessages.BuildCards(poll, media, progress, "en", Now, "https://dash.test/votes/1#votes");
        var all = Flatten(open.Components).ToArray();
        var section = Assert.Single(all.OfType<SectionComponent>());
        Assert.Equal("https://img.test/arcane.jpg", Assert.IsType<ThumbnailComponent>(section.Accessory).Media.Url);
        string text = string.Join('\n', all.OfType<TextDisplayComponent>().Select(item => item.Content));
        Assert.Contains("[Arcane](https://www.tvmaze.com/shows/82/arcane)", text, StringComparison.Ordinal);
        Assert.Contains("2021 · 2 seasons, 5 episodes", text, StringComparison.Ordinal);
        Assert.Contains("next: S2 E1 · stopped at 12:34", text, StringComparison.Ordinal);
        var menu = Assert.Single(all.OfType<SelectMenuComponent>());
        Assert.Equal((1, 2), (menu.MaxValues, menu.Options.Count));

        var ended = PollMessages.BuildCards(poll with { ClosedAt = Now }, media, progress, "en", Now, null);
        var finished = Flatten(ended.Components).ToArray();
        Assert.Empty(finished.OfType<SelectMenuComponent>());
        Assert.Single(finished.OfType<MediaGalleryComponent>());
        Assert.Contains(finished.OfType<TextDisplayComponent>(), item => item.Content.Contains("**Arcane** won with 2 of 2 votes", StringComparison.Ordinal));
    }

    [Fact]
    public void TenPostersStillFitDiscordsComponentLimit()
    {
        var options = Enumerable.Range(0, 10).Select(index => new PollOption(index, $"Show {index}", "series", null, new MediaRef("tvmaze", index.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        var media = options.ToDictionary(option => option.Media!, option => FakeCatalog.Series with { ExternalId = option.Media!.ExternalId, Title = option.Label });
        var cards = PollMessages.BuildCards(Sample(options: options, everyone: false, audience: [Kai, Organizer]), media, new Dictionary<MediaRef, WatchItem>(), "en", Now,
            "https://dash.test/votes/1#votes");
        Assert.InRange(Flatten(cards.Components).Count(), 1, 40);
        Assert.Contains(Flatten(cards.Components).OfType<TextDisplayComponent>(), item => item.Content.Contains($"<@{Kai}>", StringComparison.Ordinal));
    }

    private static IEnumerable<IMessageComponent> Flatten(IEnumerable<IMessageComponent> components)
    {
        foreach (var component in components)
        {
            yield return component;
            var children = component switch
            {
                ContainerComponent container => container.Components,
                SectionComponent section => [.. section.Components, section.Accessory],
                ActionRowComponent row => row.Components,
                _ => []
            };
            foreach (var child in Flatten(children))
                yield return child;
        }
    }
}
