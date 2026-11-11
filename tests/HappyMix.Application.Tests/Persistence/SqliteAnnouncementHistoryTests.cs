using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Application.Matches.SynchronizeMatches;
using HappyMix.Domain.Esports;
using HappyMix.Infrastructure;
using HappyMix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SqliteAnnouncementHistoryTests
{
    private static readonly DateOnly Day = new(2026, 11, 9);
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshFileIsMigratedAndMigrationCanRunAgainWithoutLosingData()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        Assert.True(File.Exists(database.DatabasePath));
        var state = State(database.Services);
        state.MarkSchedulePublished(Day);
        await database.Services.InitializeDatabaseAsync();
        await using var context = await database.Services.GetRequiredService<IDbContextFactory<HappyMixDbContext>>().CreateDbContextAsync();
        Assert.Equal(["20261109000100_InitialAnnouncementHistory", "20261111000100_AddGuildSettings"],
            await context.Database.GetAppliedMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.True(state.WasSchedulePublished(Day));
    }

    [Fact]
    public async Task AllEventKindsPersistAcrossIndependentServiceProviders()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var state = State(database.Services);
        state.MarkSchedulePublished(Day);
        state.MarkStartingSoonPublished(Esport.CounterStrike2, 1);
        state.MarkMapResultPublished(Esport.CounterStrike2, 1);
        state.MarkMatchResultPublished(Esport.CounterStrike2, 1);
        database.Services.Dispose();

        var restarted = await database.OpenAsync();
        var restored = State(restarted);
        Assert.True(restored.WasSchedulePublished(Day));
        Assert.True(restored.WasStartingSoonPublished(Esport.CounterStrike2, 1));
        Assert.True(restored.WasMapResultPublished(Esport.CounterStrike2, 1));
        Assert.True(restored.WasMatchResultPublished(Esport.CounterStrike2, 1));
        Assert.False(restored.WasSchedulePublished(Day.AddDays(1)));
        Assert.False(restored.WasMatchResultPublished(Esport.CounterStrike2, 2));
        Assert.False(restored.WasStartingSoonPublished(Esport.Dota2, 1));
        Assert.False(restored.WasMapResultPublished(Esport.Dota2, 1));
        Assert.False(restored.WasMatchResultPublished(Esport.Dota2, 1));
    }

    [Fact]
    public async Task ChannelsUseIndependentHistoryAndBootstrapPolicies()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var first = State(database.Services);
        first.MarkSchedulePublished(Day);
        first.Initialize([Finished()]);
        var second = State(await database.OpenAsync(channel: "456"));
        Assert.False(second.WasSchedulePublished(Day));
        Assert.False(second.WasMatchResultPublished(Esport.CounterStrike2, 2));
        second.Initialize([Finished()]);
        Assert.True(second.WasMatchResultPublished(Esport.CounterStrike2, 2));
    }

    [Fact]
    public async Task DuplicateAndConcurrentMarkerWritesAreIdempotent()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var state = State(database.Services);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            state.MarkMatchResultPublished(Esport.Dota2, 10))));
        await using var context = await database.Services.GetRequiredService<IDbContextFactory<HappyMixDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await context.Announcements.CountAsync());
        Assert.True(state.WasMatchResultPublished(Esport.Dota2, 10));
    }

    [Fact]
    public async Task HandlerDoesNotReplaySuccessfulPublicationsAfterRestartButNewResultsStillPublish()
    {
        using var database = await SqliteTestDatabase.CreateAsync(replay: true);
        var snapshots = new[] { Upcoming(), Upcoming() with { Game = Esport.Dota2 },
            Finished(), Finished() with { Game = Esport.Dota2 } };
        var publisher = new Publisher();
        await Run(database.Services, publisher, snapshots);
        Assert.Equal((1, 2, 2, 2), publisher.Counts);
        database.Services.Dispose();
        var restarted = await database.OpenAsync(replay: true);
        await Run(restarted, publisher, snapshots);
        Assert.Equal((1, 2, 2, 2), publisher.Counts);
        await Run(restarted, publisher, [.. snapshots, Finished() with
            { Id = 3, Games = [new EsportsGame(11, 1, "finished", true, 3216)] }]);
        Assert.Equal((1, 2, 3, 3), publisher.Counts);
    }

    [Fact]
    public async Task DefaultFirstRunSeedsOldResultsButStillPublishesScheduleAndUpcomingReminder()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var publisher = new Publisher();
        var running = Finished() with { Id = 3, Status = "running" };
        await Run(database.Services, publisher, [Upcoming(), Finished(), running]);
        Assert.Equal((1, 1, 0, 0), publisher.Counts);
        var restarted = await database.OpenAsync();
        // The running series finishes after bootstrap: publish its result, not its old map.
        await Run(restarted, publisher, [Finished(), running with { Status = "finished" }]);
        Assert.Equal((1, 1, 0, 1), publisher.Counts);
    }

    [Fact]
    public async Task BootstrapIsRecordedEvenForEmptySnapshotAndDoesNotSeedFutureResults()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var publisher = new Publisher();
        await Run(database.Services, publisher, []);
        var restarted = await database.OpenAsync();
        await Run(restarted, publisher, [Finished()]);
        Assert.Equal((1, 0, 1, 1), publisher.Counts);
    }

    [Fact]
    public async Task FailedSendIsRetriedAfterRestartWithoutRepeatingSuccessfulMessages()
    {
        using var database = await SqliteTestDatabase.CreateAsync(replay: true);
        var publisher = new Publisher { FailNextResult = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(database.Services, publisher, [Finished()]));
        Assert.False(State(database.Services).WasMatchResultPublished(Esport.CounterStrike2, 2));
        database.Services.Dispose();
        var restarted = await database.OpenAsync(replay: true);
        await Run(restarted, publisher, [Finished()]);
        Assert.Equal((1, 0, 1, 1), publisher.Counts);
    }

    [Fact]
    public async Task InvalidStoragePathFailsInitializationInsteadOfFallingBackToMemory()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var file = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "not-a-directory");
        await File.WriteAllTextAsync(file, "occupied");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:DatabasePath"] = Path.Combine(file, "history.db") }).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        await Assert.ThrowsAnyAsync<IOException>(() => provider.InitializeDatabaseAsync());
    }

    private static IMatchAnnouncementState State(ServiceProvider provider) =>
        provider.GetRequiredService<IMatchAnnouncementState>();

    private static async Task Run(ServiceProvider provider, Publisher publisher, IReadOnlyCollection<EsportsMatch> matches)
    {
        var handler = new SynchronizeMatchesCommandHandler(new Clock(), new Feed(matches), publisher, State(provider));
        await handler.Handle(new SynchronizeMatchesCommand(), CancellationToken.None);
    }

    private static EsportsMatch Upcoming() => new(1, "NAVI vs Other", Now.AddMinutes(20), "not_started",
        "League", "Tournament", [new EsportsOpponent(3216, "NAVI")], [], [], null);

    private static EsportsMatch Finished() => Upcoming() with { Id = 2, BeginAt = Now.AddHours(-2),
        Status = "finished", WinnerId = 3216, Games = [new EsportsGame(10, 1, "finished", true, 3216)] };

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Feed(IReadOnlyCollection<EsportsMatch> matches) : IPandaScoreClient
    {
        public Task<EsportsMatchDay> GetMatchesForDayAsync(DateTimeOffset referenceTime, CancellationToken cancellationToken) =>
            Task.FromResult(new EsportsMatchDay(Day, matches));
    }

    private sealed class Publisher : IMatchAnnouncementPublisher
    {
        private int _schedules, _starts, _maps, _results;
        public bool FailNextResult { get; set; }
        public (int Schedules, int Starts, int Maps, int Results) Counts => (_schedules, _starts, _maps, _results);
        public Task PublishDailyScheduleAsync(DateOnly matchDate, IReadOnlyCollection<EsportsMatch> matches, CancellationToken cancellationToken)
        { _schedules++; return Task.CompletedTask; }
        public Task PublishStartingSoonAsync(EsportsMatch match, CancellationToken cancellationToken)
        { _starts++; return Task.CompletedTask; }
        public Task PublishMapResultAsync(EsportsMatch match, EsportsGame game, CancellationToken cancellationToken)
        { _maps++; return Task.CompletedTask; }
        public Task PublishMatchResultAsync(EsportsMatch match, CancellationToken cancellationToken)
        {
            if (FailNextResult) { FailNextResult = false; throw new HttpRequestException("Simulated failed send"); }
            _results++; return Task.CompletedTask;
        }
    }
}
