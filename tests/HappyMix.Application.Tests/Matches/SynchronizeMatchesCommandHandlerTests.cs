using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Application.Matches.SynchronizeMatches;
using HappyMix.Domain.CounterStrike;

namespace HappyMix.Application.Tests.Matches;

public sealed class SynchronizeMatchesCommandHandlerTests
{
    [Fact]
    public async Task HandlePublishesEachAnnouncementOnlyOnce()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var runningMatch = CreateMatch(
            id: 1,
            status: "running",
            beginAt: now.AddMinutes(20),
            games: [new CounterStrikeGame(11, 1, "finished", true, 101)]);
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
            new StubPandaScoreClient(new CounterStrikeMatchDay(
                new DateOnly(2026, 9, 3),
                [runningMatch, finishedMatch])),
            publisher,
            state);

        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);

        Assert.Equal(1, publisher.ScheduleCount);
        Assert.Equal(1, publisher.StartingSoonCount);
        Assert.Equal(1, publisher.MapResultCount);
        Assert.Equal(1, publisher.MatchResultCount);
    }

    private static CounterStrikeMatch CreateMatch(
        long id,
        string status,
        DateTimeOffset beginAt,
        IReadOnlyCollection<CounterStrikeGame> games,
        long? winnerId = null) => new(
            id,
            $"Team One vs Team Two ({id})",
            beginAt,
            status,
            "Test League",
            "Test Tournament",
            [new CounterStrikeOpponent(101, "Team One"), new CounterStrikeOpponent(102, "Team Two")],
            [new CounterStrikeTeamResult(101, 1), new CounterStrikeTeamResult(102, 0)],
            games,
            winnerId);

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class StubPandaScoreClient(CounterStrikeMatchDay matchDay) : IPandaScoreClient
    {
        public Task<CounterStrikeMatchDay> GetMatchesForDayAsync(
            DateTimeOffset referenceTime,
            CancellationToken cancellationToken) => Task.FromResult(matchDay);
    }

    private sealed class RecordingPublisher : IMatchAnnouncementPublisher
    {
        public int ScheduleCount { get; private set; }

        public int StartingSoonCount { get; private set; }

        public int MapResultCount { get; private set; }

        public int MatchResultCount { get; private set; }

        public Task PublishDailyScheduleAsync(
            DateOnly matchDate,
            IReadOnlyCollection<CounterStrikeMatch> matches,
            CancellationToken cancellationToken)
        {
            ScheduleCount++;
            return Task.CompletedTask;
        }

        public Task PublishStartingSoonAsync(
            CounterStrikeMatch match,
            CancellationToken cancellationToken)
        {
            StartingSoonCount++;
            return Task.CompletedTask;
        }

        public Task PublishMapResultAsync(
            CounterStrikeMatch match,
            CounterStrikeGame game,
            CancellationToken cancellationToken)
        {
            MapResultCount++;
            return Task.CompletedTask;
        }

        public Task PublishMatchResultAsync(
            CounterStrikeMatch match,
            CancellationToken cancellationToken)
        {
            MatchResultCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAnnouncementState : IMatchAnnouncementState
    {
        private readonly HashSet<DateOnly> _schedules = [];
        private readonly HashSet<long> _startingSoon = [];
        private readonly HashSet<long> _mapResults = [];
        private readonly HashSet<long> _matchResults = [];

        public bool WasSchedulePublished(DateOnly matchDate) => _schedules.Contains(matchDate);

        public bool WasStartingSoonPublished(long matchId) => _startingSoon.Contains(matchId);

        public bool WasMapResultPublished(long gameId) => _mapResults.Contains(gameId);

        public bool WasMatchResultPublished(long matchId) => _matchResults.Contains(matchId);

        public void MarkSchedulePublished(DateOnly matchDate) => _schedules.Add(matchDate);

        public void MarkStartingSoonPublished(long matchId) => _startingSoon.Add(matchId);

        public void MarkMapResultPublished(long gameId) => _mapResults.Add(gameId);

        public void MarkMatchResultPublished(long matchId) => _matchResults.Add(matchId);
    }
}
