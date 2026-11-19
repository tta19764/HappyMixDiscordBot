using HappyMix.Application.Abstractions.Logs;
using HappyMix.Application.Guilds;
using HappyMix.Application.Polls;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Application.Tests.Polls;
using HappyMix.Bot.Logging;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HappyMix.Application.Tests.Logging;

public sealed partial class LogsTests
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Vote {PollId} in server {GuildId} posted with key=hunter2")]
    private static partial void LogPosted(ILogger logger, Guid pollId, ulong guildId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "not stored")]
    private static partial void LogNoise(ILogger logger);

    [Theory]
    [InlineData("HappyMix.Bot.Jobs.PublishPollsJob", LogLevel.Information, true)]
    [InlineData("HappyMix.Application.Logging.CommandLoggingBehavior", LogLevel.Debug, false)]
    [InlineData("Microsoft.AspNetCore.Hosting", LogLevel.Information, false)]
    [InlineData("Microsoft.AspNetCore.Hosting", LogLevel.Warning, true)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Error, false)]
    [InlineData("System.Net.Http.HttpClient.Media.LogicalHandler", LogLevel.Information, false)]
    public void OnlyUsefulEntriesAreStored(string category, LogLevel level, bool stored) =>
        Assert.Equal(stored, DatabaseLoggerProvider.Stores(category, level));

    [Theory]
    [InlineData("https://api.themoviedb.org/3/search/movie?query=dune&api_key=abc123", "https://api.themoviedb.org/3/search/movie?query=dune&api_key=***")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "Authorization: Bearer ***")]
    [InlineData("token MTIzNDU2Nzg5MDEyMzQ1Njc4OTAxMjM0.Gabcde.abcdefghijklmnopqrstuvwxyz123", "token ***")]
    [InlineData("client_secret=s3cret&grant_type=client_credentials", "client_secret=***&grant_type=client_credentials")]
    public void SecretsAreMasked(string text, string expected) => Assert.Equal(expected, DatabaseLoggerProvider.Redact(text));

    [Fact]
    public void ReleaseNotesStopOnceEveryNoteIsPosted()
    {
        var state = new Dictionary<string, ulong>(StringComparer.Ordinal) { ["channel"] = 5, ["v0.0.9/0"] = 1, ["v0.0.9/1"] = 2 };
        Assert.True(HappyMix.Bot.Jobs.PublishReleaseNotesJob.HasPending("v0.0.9", 3, state));
        state["v0.0.9/2"] = 3;
        Assert.False(HappyMix.Bot.Jobs.PublishReleaseNotesJob.HasPending("v0.0.9", 3, state));
        Assert.True(HappyMix.Bot.Jobs.PublishReleaseNotesJob.HasPending("v0.0.10", 1, state));
    }

    [Fact]
    public async Task EntriesKeepTheirServerAndUserFromMessagesAndScopes()
    {
        using var provider = new DatabaseLoggerProvider();
        var logger = provider.CreateLogger("HappyMix.Bot.Services.PollMessages");
        using (logger.BeginScope(new Dictionary<string, object> { ["UserId"] = 42UL }))
            LogPosted(logger, Guid.Empty, 7UL);
        LogNoise(logger);

        Assert.True(provider.Reader.TryRead(out var entry));
        Assert.Equal((7UL, 42UL, "discord", LogSeverity.Information), (entry!.GuildId, entry.UserId, entry.Area, entry.Level));
        Assert.EndsWith("posted with key=***", entry.Message, StringComparison.Ordinal);
        Assert.Contains("PollId", entry.Properties!, StringComparison.Ordinal);
        Assert.DoesNotContain("GuildId", entry.Properties!, StringComparison.Ordinal);
        Assert.False(provider.Reader.TryRead(out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ServersOnlySeeTheirOwnEntriesAndOwnersCanAddBotWideOnes()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<ILogStore>();
        var now = DateTimeOffset.UtcNow;
        LogEntry Entry(ulong? guild, LogSeverity level, string area, string message, int minutesAgo = 0) =>
            new(0, now.AddMinutes(-minutesAgo), level, area, "Test", message, null, guild, 5, null);
        await store.WriteAsync([
            Entry(1, LogSeverity.Information, "commands", "CreatePollCommand succeeded"),
            Entry(1, LogSeverity.Warning, "discord", "Vote couldn't be posted"),
            Entry(2, LogSeverity.Error, "jobs", "Other server failed"),
            Entry(null, LogSeverity.Warning, "integrations", "Catalog lookup failed"),
            Entry(1, LogSeverity.Information, "jobs", "Old entry", minutesAgo: 60 * 30)], CancellationToken.None);

        var mine = await store.ReadAsync(new LogFilter(1, false, Since: now.AddDays(-1)), CancellationToken.None);
        Assert.Equal(["Vote couldn't be posted", "CreatePollCommand succeeded"], mine.Select(entry => entry.Message));
        Assert.Equal(["Vote couldn't be posted"], (await store.ReadAsync(new LogFilter(1, false, LogSeverity.Warning), CancellationToken.None)).Select(entry => entry.Message));
        Assert.Equal(["CreatePollCommand succeeded"], (await store.ReadAsync(new LogFilter(1, false, Search: "poll"), CancellationToken.None)).Select(entry => entry.Message));
        Assert.Equal(["Old entry"], (await store.ReadAsync(new LogFilter(1, false, Area: "jobs"), CancellationToken.None)).Select(entry => entry.Message));
        Assert.Contains("Catalog lookup failed", (await store.ReadAsync(new LogFilter(1, true), CancellationToken.None)).Select(entry => entry.Message));
        Assert.DoesNotContain("Other server failed", (await store.ReadAsync(new LogFilter(1, true, LogSeverity.Debug), CancellationToken.None)).Select(entry => entry.Message));

        var page = await store.ReadAsync(new LogFilter(1, false, Limit: 1), CancellationToken.None);
        var next = await store.ReadAsync(new LogFilter(1, false, Before: page[0].Id, Limit: 1), CancellationToken.None);
        Assert.Equal("Vote couldn't be posted", next.Single().Message);
        Assert.Equal(page[0].Id, (await store.ReadAsync(new LogFilter(1, false, After: next[0].Id), CancellationToken.None)).Single().Id);

        await store.PruneAsync(now.AddDays(-1), 1000, CancellationToken.None);
        Assert.DoesNotContain("Old entry", (await store.ReadAsync(new LogFilter(1, false), CancellationToken.None)).Select(entry => entry.Message));
        await store.PruneAsync(now.AddDays(-1), 1, CancellationToken.None);
        Assert.Single(await store.ReadAsync(new LogFilter(null, true, LogSeverity.Debug), CancellationToken.None));
    }

    [Fact]
    public async Task CommandsAreLoggedWithServerUserAndOutcome()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var provider = new DatabaseLoggerProvider();
        var storage = database.Services;
        using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(provider).SetMinimumLevel(LogLevel.Debug))
            .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Guilds.IGuildGroupStore>())
            .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Guilds.IAccountLinkStore>())
            .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Polls.IPollStore>())
            .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Watch.IWatchStore>())
            .AddSingleton<HappyMix.Application.Abstractions.Media.IMediaCatalog>(new FakeCatalog())
            .AddSingleton<HappyMix.Application.Abstractions.IClock>(new PollTests.MutableClock())
            .AddApplication().BuildServiceProvider();
        var sender = services.GetRequiredService<ISender>();
        var actor = new GroupActor(1, 20, false);

        await sender.Send(new CreatePollCommand(actor, null, "Pizza?", ["Yes", "No"], false, TimeSpan.FromHours(1), [], true, Guid.NewGuid()));
        await sender.Send(new CreatePollCommand(actor, null, "Pizza?", ["Only one"], false, TimeSpan.FromHours(1), [], true, Guid.NewGuid()));
        await sender.Send(new GetPollBoardQuery(actor, null));

        var entries = new List<LogEntry>();
        while (provider.Reader.TryRead(out var entry))
            if (entry.Category.Contains("CommandLoggingBehavior", StringComparison.Ordinal))
                entries.Add(entry);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal((1UL, 20UL, "commands"), (entry.GuildId!.Value, entry.UserId!.Value, entry.Area)));
        Assert.Equal(LogSeverity.Information, entries[0].Level);
        Assert.Equal(LogSeverity.Warning, entries[1].Level);
        Assert.Contains("Polls_OptionCount", entries[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Pizza", string.Join(" ", entries.Select(entry => entry.Message + entry.Properties)), StringComparison.Ordinal);
    }
}
