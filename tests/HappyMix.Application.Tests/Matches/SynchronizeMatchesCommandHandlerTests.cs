using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Application.Matches.SynchronizeMatches;
using HappyMix.Domain.Esports;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Matches;

public sealed class SynchronizeMatchesCommandHandlerTests
{
    [Fact]
    public async Task FinishedMapWithWinnerIsReportedBeforeProviderSetsComplete()
    {
        var now = new DateTimeOffset(2026, 11, 12, 9, 40, 0, TimeSpan.Zero);
        var match = CreateMatch(1762461, "running", now.AddHours(-2),
            [new EsportsGame(236680, 2, "finished", false, 3240),
             new EsportsGame(236681, 3, "finished", false, null)]);
        var day = new EsportsMatchDay(new DateOnly(2026, 11, 12), [match]);
        var publisher = new RecordingPublisher();
        var handler = new SynchronizeMatchesCommandHandler(new StubClock(now), new StubPandaScoreClient(day),
            publisher, new FakeAnnouncementState());

        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);

        Assert.Equal(1, publisher.MapResultCount);
    }

    [Fact]
    public async Task GuildReminderUsesCustomLeadTimeAndSuppressesDisabledNotifications()
    {
        var now = new DateTimeOffset(2026, 9, 3, 23, 30, 0, TimeSpan.Zero);
        var day = new EsportsMatchDay(new DateOnly(2026, 9, 3),
            [CreateMatch(1, "not_started", now.AddMinutes(45), []),
             CreateMatch(2, "not_started", now.AddMinutes(46), []),
             CreateMatch(3, "finished", now.AddHours(-2), [new EsportsGame(11, 1, "finished", true, 101)], 101)]);
        var publisher = new RecordingPublisher();
        var handler = new SynchronizeMatchesCommandHandler(new StubClock(now), new StubPandaScoreClient(day),
            publisher, new FakeAnnouncementState());
        var preferences = new GuildEsportsPreferences(Notifications: EsportsNotifications.Reminders, ReminderMinutes: 45);

        await handler.PublishAsync(day, now, preferences, TimeZoneInfo.Utc, CancellationToken.None);
        await handler.PublishAsync(day, now, preferences, TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal(1, publisher.StartingSoonCount);
        Assert.Equal(0, publisher.ScheduleCount);
        Assert.Equal(0, publisher.MapResultCount);
        Assert.Equal(0, publisher.MatchResultCount);
    }

    [Fact]
    public async Task HandlePublishesEachAnnouncementOnlyOnce()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var runningMatch = CreateMatch(
            id: 1,
            status: "running",
            beginAt: now.AddMinutes(20),
            games: [new EsportsGame(11, 1, "finished", true, 101)]);
        var finishedMatch = CreateMatch(
            id: 2,
            status: "finished",
            beginAt: now.AddHours(-2),
            games: [],
            winnerId: 101);
        var publisher = new RecordingPublisher();
        var state = new FakeAnnouncementState();
        var handler = new SynchronizeMatchesCommandHandler(
            new StubClock(now),
            new StubPandaScoreClient(new EsportsMatchDay(
                new DateOnly(2026, 9, 3),
                [runningMatch, finishedMatch, CreateMatch(3, "not_started", now.AddMinutes(30), [])])),
            publisher,
            state);

        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);

        Assert.Equal(1, publisher.ScheduleCount);
        Assert.Equal(1, publisher.StartingSoonCount);
        Assert.Equal(1, publisher.MapResultCount);
        Assert.Equal(1, publisher.MatchResultCount);
    }

    [Fact]
    public async Task IdenticalProviderIdsInDifferentGamesDoNotSuppressNotifications()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var cs = CreateMatch(1, "finished", now.AddHours(-2),
            [new EsportsGame(11, 1, "finished", true, 101)], 101);
        var dota = cs with { Game = Esport.Dota2 };
        var publisher = new RecordingPublisher();
        var handler = new SynchronizeMatchesCommandHandler(new StubClock(now),
            new StubPandaScoreClient(new EsportsMatchDay(new DateOnly(2026, 9, 3), [cs, dota])),
            publisher, new FakeAnnouncementState());
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        Assert.Equal(1, publisher.ScheduleCount);
        Assert.Equal(2, publisher.MapResultCount);
        Assert.Equal(2, publisher.MatchResultCount);
    }

    [Theory]
    [InlineData("not_started", 30, 1)]
    [InlineData("not_started", 31, 0)]
    [InlineData("not_started", 0, 0)]
    [InlineData("not_started", -1, 0)]
    [InlineData("running", 20, 0)]
    [InlineData("canceled", 20, 0)]
    [InlineData("postponed", 20, 0)]
    [InlineData("finished", 20, 0)]
    public async Task OnlyUpcomingMatchesWithinReminderWindowAreAnnounced(string status, int minutes, int expected)
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var cs = CreateMatch(1, status, now.AddMinutes(minutes), []);
        var publisher = new RecordingPublisher();
        var handler = new SynchronizeMatchesCommandHandler(new StubClock(now),
            new StubPandaScoreClient(new EsportsMatchDay(new DateOnly(2026, 9, 3), [cs, cs with { Game = Esport.Dota2 }])),
            publisher, new FakeAnnouncementState());
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        Assert.Equal(expected * 2, publisher.StartingSoonCount);
    }

    [Fact]
    public async Task RealAnnouncementStateSeparatesDisciplines()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        using var database = await Persistence.SqliteTestDatabase.CreateAsync(replay: true);
        var state = database.Services.GetRequiredService<IMatchAnnouncementState>();
        var cs = CreateMatch(1, "finished", now, [new EsportsGame(2, 1, "finished", true, 101)], 101);
        var publisher = new RecordingPublisher();
        var handler = new SynchronizeMatchesCommandHandler(new StubClock(now),
            new StubPandaScoreClient(new EsportsMatchDay(new DateOnly(2026, 9, 3), [cs, cs with { Game = Esport.Dota2 }])),
            publisher, state);
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        Assert.Equal(2, publisher.MatchResultCount);
        Assert.Equal(2, publisher.MapResultCount);
    }

    [Fact]
    public async Task FailedPublicationCanBeRetriedWithoutRepeatingSuccessfulMapResults()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var match = CreateMatch(1, "finished", now, [new EsportsGame(2, 1, "finished", true, 101)], 101);
        var publisher = new RecordingPublisher { FailNextMatchResult = true };
        var handler = new SynchronizeMatchesCommandHandler(new StubClock(now),
            new StubPandaScoreClient(new EsportsMatchDay(new DateOnly(2026, 9, 3), [match])),
            publisher, new FakeAnnouncementState());
        await Assert.ThrowsAsync<HttpRequestException>(() => handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None));
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        Assert.Equal(1, publisher.ScheduleCount);
        Assert.Equal(1, publisher.MapResultCount);
        Assert.Equal(1, publisher.MatchResultCount);
    }

    private static EsportsMatch CreateMatch(
        long id,
        string status,
        DateTimeOffset beginAt,
        IReadOnlyCollection<EsportsGame> games,
        long? winnerId = null) => new(
            id,
            $"Team One vs Team Two ({id})",
            beginAt,
            status,
            "Test League",
            "Test Tournament",
            [new EsportsOpponent(101, "Team One"), new EsportsOpponent(102, "Team Two")],
            [new EsportsTeamResult(101, 1), new EsportsTeamResult(102, 0)],
            games,
            winnerId);

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class StubPandaScoreClient(EsportsMatchDay matchDay) : IPandaScoreClient
    {
        public Task<EsportsMatchDay> GetMatchesForDayAsync(
            DateTimeOffset referenceTime,
            CancellationToken cancellationToken) => Task.FromResult(matchDay);
    }

    private sealed class RecordingPublisher : IMatchAnnouncementPublisher
    {
        public bool FailNextMatchResult { get; set; }
        public int ScheduleCount { get; private set; }

        public int StartingSoonCount { get; private set; }

        public int MapResultCount { get; private set; }

        public int MatchResultCount { get; private set; }

        public Task PublishDailyScheduleAsync(
            DateOnly matchDate,
            IReadOnlyCollection<EsportsMatch> matches,
            CancellationToken cancellationToken)
        {
            ScheduleCount++;
            return Task.CompletedTask;
        }

        public Task PublishStartingSoonAsync(
            EsportsMatch match,
            CancellationToken cancellationToken)
        {
            StartingSoonCount++;
            return Task.CompletedTask;
        }

        public Task PublishMapResultAsync(
            EsportsMatch match,
            EsportsGame game,
            CancellationToken cancellationToken)
        {
            MapResultCount++;
            return Task.CompletedTask;
        }

        public Task PublishMatchResultAsync(
            EsportsMatch match,
            CancellationToken cancellationToken)
        {
            if (FailNextMatchResult)
            {
                FailNextMatchResult = false;
                throw new HttpRequestException("Simulated Discord delivery failure");
            }
            MatchResultCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAnnouncementState : IMatchAnnouncementState
    {
        public void Initialize(IReadOnlyCollection<EsportsMatch> matches) { }
        private readonly HashSet<DateOnly> _schedules = [];
        private readonly HashSet<(Esport Game, long Id)> _startingSoon = [];
        private readonly HashSet<(Esport Game, long Id)> _mapResults = [];
        private readonly HashSet<(Esport Game, long Id)> _matchResults = [];

        public bool WasSchedulePublished(DateOnly matchDate) => _schedules.Contains(matchDate);

        public bool WasStartingSoonPublished(Esport game, long matchId) => _startingSoon.Contains((game, matchId));

        public bool WasMapResultPublished(Esport game, long gameId) => _mapResults.Contains((game, gameId));

        public bool WasMatchResultPublished(Esport game, long matchId) => _matchResults.Contains((game, matchId));

        public void MarkSchedulePublished(DateOnly matchDate) => _schedules.Add(matchDate);

        public void MarkStartingSoonPublished(Esport game, long matchId) => _startingSoon.Add((game, matchId));

        public void MarkMapResultPublished(Esport game, long gameId) => _mapResults.Add((game, gameId));

        public void MarkMatchResultPublished(Esport game, long matchId) => _matchResults.Add((game, matchId));
    }
}
