using System.Text.Json;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Live;
using HappyMix.Application.Abstractions.Quiet;
using HappyMix.Application.Guilds;
using HappyMix.Application.Quiet;
using HappyMix.Application.Tests.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Players;

/// <summary>Do not disturb: ranges, holding personal messages, the relevance check on release, and the boards.</summary>
public sealed class DoNotDisturbTests
{
    private const ulong Guild = 1, Kai = 10, Mira = 20;
    private const string Kyiv = "Europe/Kyiv";
    // Monday 23 November 2026, 21:30 UTC = 23:30 in Kyiv.
    private static readonly DateTimeOffset MondayNight = new(2026, 11, 23, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public void OvernightWeeklyHoursAndDatesChainIntoOneStretch()
    {
        var weeknights = new QuietSchedule(
            [new QuietWeekly(Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(23, 0), new TimeOnly(8, 0), Kyiv)], []);
        // 23:30 Monday → 08:00 Tuesday Kyiv (06:00 UTC).
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 6, 0, 0, TimeSpan.Zero), QuietHours.Until(weeknights, MondayNight));
        Assert.Null(QuietHours.Until(weeknights, MondayNight.AddHours(-1)));
        Assert.Null(QuietHours.Until(weeknights, MondayNight.AddHours(9)));

        // A dated range starting when the weekly one ends extends the stretch.
        var extended = weeknights with { Dates = [new QuietDates(Guid.NewGuid(), new(2026, 11, 24, 6, 0, 0, TimeSpan.Zero), new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero))] };
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), QuietHours.Until(extended, MondayNight));
    }

    [Fact]
    public async Task RangesAreAddedListedAndRemoved()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Assert.Equal(5, (await sender.Send(new AddQuietWeeklyCommand(Kai,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], "23:00", "08:00", false, Kyiv))).Value);
        Assert.Equal(1, (await sender.Send(new AddQuietDatesCommand(Kai, new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 26), "", "", true, Kyiv))).Value);
        Assert.Equal(3, (await sender.Send(new AddQuietDatesCommand(Kai, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3), "13", "15", false, Kyiv))).Value);
        Assert.True((await sender.Send(new AddQuietWeeklyCommand(Kai, [], "23", "8", false, Kyiv))).IsFailure);
        Assert.True((await sender.Send(new AddQuietDatesCommand(Kai, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), "", "", true, Kyiv))).IsFailure);

        var overview = (await sender.Send(new GetQuietOverviewQuery(Kai))).Value;
        Assert.Equal((5, 4), (overview.Schedule.Weekly.Count, overview.Schedule.Dates.Count));
        Assert.NotNull(overview.QuietUntil);
        Assert.True((await sender.Send(new RemoveQuietCommand(Kai, overview.Schedule.Weekly[0].Id))).IsSuccess);
        Assert.True((await sender.Send(new RemoveQuietCommand(Mira, overview.Schedule.Weekly[1].Id))).IsFailure);
    }

    [Fact]
    public async Task HeldGroupNoticesGoOutAfterwardsOnlyIfStillOpen()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync(joinRequests: [Mira]);
        await sender.Send(new AddQuietWeeklyCommand(Kai, [DayOfWeek.Monday], "23:00", "08:00", false, Kyiv));

        var request = new GroupNotice(GroupNoticeKind.JoinRequested, Guild, groupId, "Squad", Kai, Mira);
        Assert.True((await sender.Send(new HoldIfQuietCommand(Kai, HeldKinds.Group, "join:20", Json(request)))).Value);
        // Mira asks twice: Kai gets one message, not two.
        Assert.True((await sender.Send(new HoldIfQuietCommand(Kai, HeldKinds.Group, "join:20", Json(request)))).Value);
        Assert.False((await sender.Send(new HoldIfQuietCommand(Mira, HeldKinds.Group, null, Json(request)))).Value);
        Assert.Equal(1, (await sender.Send(new GetQuietOverviewQuery(Kai))).Value.Held);

        // Still night: nothing goes out.
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await sender.Send(new ReleaseHeldMessagesCommand())).Value);

        fixture.Clock.Advance(TimeSpan.FromHours(8));
        Assert.Equal(1, (await sender.Send(new ReleaseHeldMessagesCommand())).Value);
        Assert.Equal(Kai, Assert.Single(fixture.Sent).UserId);

        // A request answered during the night is dropped instead.
        fixture.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromHours(9));
        await sender.Send(new HoldIfQuietCommand(Kai, HeldKinds.Group, "join:20", Json(request)));
        await fixture.Groups.UpdateAsync(Guild, "Squad", group => group! with { JoinRequests = [] });
        fixture.Clock.Advance(TimeSpan.FromHours(9));
        Assert.Equal(0, (await sender.Send(new ReleaseHeldMessagesCommand())).Value);
        Assert.Equal(0, (await sender.Send(new GetQuietOverviewQuery(Kai))).Value.Held);
    }

    [Fact]
    public async Task HeldLiveNoticesNeedThePlayerStillInTheGame()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        await sender.Send(new HappyMix.Application.Live.SetLiveSharingCommand(new GroupActor(Guild, Kai, false), groupId, Share: true, Notify: false));
        await sender.Send(new HappyMix.Application.Live.SetLiveSharingCommand(new GroupActor(Guild, Mira, false), groupId, Share: false, Notify: true));
        await sender.Send(new AddQuietWeeklyCommand(Mira, [DayOfWeek.Monday], "23:00", "08:00", false, Kyiv));

        // Kai starts Dota at night: the DM to Mira waits.
        await sender.Send(new HappyMix.Application.Live.ReportPlayingCommand(Kai, "Dota 2", "discord"));
        Assert.Equal(1, (await sender.Send(new GetQuietOverviewQuery(Mira))).Value.Held);

        // Kai stops before Mira's morning: the notice is no longer true and is dropped.
        await sender.Send(new HappyMix.Application.Live.ReportPlayingCommand(Kai, null, "discord"));
        fixture.Clock.Advance(TimeSpan.FromHours(9));
        Assert.Equal(0, (await sender.Send(new ReleaseHeldMessagesCommand())).Value);
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public async Task BoardsShowDoNotDisturb()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        await fixture.CreateGroupAsync();
        await sender.Send(new AddQuietWeeklyCommand(Mira, [DayOfWeek.Monday], "23:00", "08:00", false, Kyiv));
        var board = (await sender.Send(new GetMembersAvailabilityQuery(Guild, [Kai, Mira], new DateOnly(2026, 11, 24), Kyiv))).Value;
        var quiet = Assert.Single(board.Rows.Single(row => row.UserId == Mira).QuietRanges);
        Assert.Equal((new TimeOnly(0, 0), new TimeOnly(8, 0), true), (quiet.Start, quiet.End, quiet.FromPreviousDay));
        Assert.Empty(board.Rows.Single(row => row.UserId == Kai).QuietRanges);
        Assert.All(board.FreeByHour, count => Assert.Equal(0, count));

        // /availability who lists the same: free time first, then the do-not-disturb hours.
        Assert.Equal("🌙 do not disturb 00:00–08:00", HappyMix.Bot.Interactions.AvailabilityModule.Line(board.Rows.Single(row => row.UserId == Mira)));
        var both = new BoardRow(Kai, [new BoardRange(AvailabilitySource.OneOff, new TimeOnly(18, 0), new TimeOnly(23, 0), false, false, false, true)],
            [new BoardRange(AvailabilitySource.OneOff, new TimeOnly(23, 0), new TimeOnly(0, 0), false, true, false)]);
        Assert.Equal("18:00–23:00 (maybe) · 🌙 do not disturb 23:00–00:00", HappyMix.Bot.Interactions.AvailabilityModule.Line(both));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, HeldKinds.Json);

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public LiveTests.MutableClock Clock { get; } = new(MondayNight);
        public List<HeldMessage> Sent { get; } = [];
        public ISender Sender => application.GetRequiredService<ISender>();
        public IGuildGroupStore Groups => database.Services.GetRequiredService<IGuildGroupStore>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton(storage.GetRequiredService<IAvailabilityStore>())
                .AddSingleton(storage.GetRequiredService<HappyMix.Application.Abstractions.Players.IPlayerGameStore>())
                .AddSingleton(storage.GetRequiredService<ILiveStore>())
                .AddSingleton(storage.GetRequiredService<IQuietStore>())
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton<HappyMix.Application.Abstractions.Players.ISteamDirectory>(new PlayerGameTests.FakeSteam())
                .AddSingleton<ILiveNotifier, HoldingLiveNotifier>()
                .AddSingleton<IHeldMessageSender>(new Recorder(HeldKinds.Group, fixture.Sent))
                .AddSingleton<IHeldMessageSender>(new Recorder(HeldKinds.Live, fixture.Sent))
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        public async Task<Guid> CreateGroupAsync(ulong[]? joinRequests = null)
        {
            await Groups.UpdateAsync(Guild, "Squad", _ => new GuildGroup("Squad", Kai, [Kai, Mira], [], JoinRequests: joinRequests));
            var groupId = (await Groups.ListAsync(Guild)).Single().Id;
            await Groups.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), groupId, "Dota 2", GroupGame.SteamProvider, "570"));
            return groupId;
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }

    /// <summary>Holds each DM the way the Discord notifier does.</summary>
    private sealed class HoldingLiveNotifier(IServiceProvider services) : ILiveNotifier
    {
        public async Task NotifyAsync(LiveNotice notice, CancellationToken cancellationToken)
        {
            var sender = services.GetRequiredService<ISender>();
            foreach (ulong recipient in notice.DmRecipients)
                await sender.Send(new HoldIfQuietCommand(recipient, HeldKinds.Live, $"{notice.PlayerId}:{notice.Game}",
                    Json(notice with { DmRecipients = [recipient] })), cancellationToken);
        }
    }

    private sealed class Recorder(string kind, List<HeldMessage> sent) : IHeldMessageSender
    {
        public string Kind => kind;

        public Task<HeldDelivery> SendAsync(HeldMessage message, CancellationToken cancellationToken)
        {
            sent.Add(message);
            return Task.FromResult(HeldDelivery.Sent);
        }
    }
}
