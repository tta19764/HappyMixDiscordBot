using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Events;
using HappyMix.Application.Guilds;
using HappyMix.Application.Tests.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Events;

public sealed class EventSchedulingTests
{
    private const ulong Guild = 1, Organizer = 10, Kai = 20, Mira = 30, Taro = 40, Outsider = 99;
    private const string Kyiv = "Europe/Kyiv";
    private static readonly DateOnly Thursday = new(2026, 11, 19);

    [Fact]
    public async Task NotificationManagementRequiresOrganizerOrAdministratorInTheSameGuild()
    {
        using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Sender.Send(Create(Organizer))).Value.EventId;
        Assert.True((await fixture.Sender.Send(new CanManageNotificationsQuery(Guild, Organizer, false, "event", id))).Value);
        Assert.False((await fixture.Sender.Send(new CanManageNotificationsQuery(Guild, Kai, false, "event", id))).Value);
        Assert.True((await fixture.Sender.Send(new CanManageNotificationsQuery(Guild, Kai, true, "event", id))).Value);
        Assert.False((await fixture.Sender.Send(new CanManageNotificationsQuery(999, Kai, true, "event", id))).Value);
    }

    [Fact]
    public async Task RecurringAcceptanceCoversSeriesAndWithdrawalsOnlyAffectOneDate()
    {
        using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Sender.Send(Create(Organizer) with { Invitees = [Kai],
            Recurrence = EventRecurrence.Daily, Until = Thursday.AddDays(2) })).Value.EventId;
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, true));
        var details = (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, id))).Value.Details;
        Assert.Equal(3, details.Responses.Count(item => item.Accepted));
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday.AddDays(1), false));
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, true));
        details = (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, id))).Value.Details;
        Assert.Equal(2, details.Responses.Count(item => item.Accepted));
        Assert.True(details.Responses.Single(item => item.Date == Thursday.AddDays(1)).Withdrawn);
    }

    [Fact]
    public async Task GroupEventsInviteEveryOtherMemberAndAppearPendingForEveryone()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync(Organizer, [Kai, Mira, Taro]);

        var created = await fixture.Sender.Send(Create(Organizer) with { GroupId = group, Invitees = [] });

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Description : null);
        Assert.Equal(3, created.Value.Invited);
        var view = (await fixture.Sender.Send(new GetEventQuery(Guild, Kai, created.Value.EventId))).Value;
        Assert.True(view.IsInvitee);
        Assert.Equal(OccurrenceStatus.Pending, Assert.Single(view.Details.Occurrences).Status);
        Assert.Equal([Kai, Mira, Taro], view.Details.Invitees.Order());
        Assert.Single((await fixture.Sender.Send(new GetGroupEventsQuery(Guild, group))).Value);
        Assert.Single((await fixture.Sender.Send(new GetMemberEventsQuery(Guild, Organizer))).Value.Events);
        Assert.True((await fixture.Sender.Send(new GetEventQuery(Guild, Outsider, created.Value.EventId))).IsFailure);
        Assert.True((await fixture.Sender.Send(Create(Outsider) with { GroupId = group, RequestId = Guid.NewGuid() })).IsFailure);
    }

    [Fact]
    public async Task StandaloneEventsInviteExactlyThePeopleChosenAndRepeatedSubmitsCreateOneEvent()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = Create(Organizer) with { Invitees = [Kai, Mira, Organizer, Kai] };

        var first = await fixture.Sender.Send(command);
        var again = await fixture.Sender.Send(command);

        Assert.Equal(2, first.Value.Invited);
        Assert.True(again.Value.AlreadyExisted);
        var events = (await fixture.Sender.Send(new GetMemberEventsQuery(Guild, Organizer))).Value.Events;
        Assert.Equal([Kai, Mira], Assert.Single(events).Invitees.Order());
        Assert.True((await fixture.Sender.Send(Create(Organizer) with { RequestId = Guid.NewGuid(), Invitees = [Organizer] })).IsFailure);
    }

    [Fact]
    public async Task AnswersAreIdempotentWithdrawalsAreVisibleAndOnlyInviteesCanAnswer()
    {
        using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Sender.Send(Create(Organizer) with { Invitees = [Kai, Mira, Taro] })).Value.EventId;

        Assert.True((await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, true))).Value.Changed);
        Assert.False((await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, true))).Value.Changed);
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Mira, id, Thursday, true));
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Taro, id, Thursday, false));
        Assert.True((await fixture.Sender.Send(new RespondToEventCommand(Guild, Outsider, id, Thursday, true))).IsFailure);

        // Withdrawing while pending.
        var withdrawn = (await fixture.Sender.Send(new RespondToEventCommand(Guild, Mira, id, Thursday, false))).Value;
        Assert.True(withdrawn.Withdrawn);
        var details = (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, id))).Value.Details;
        Assert.Equal(new EventCounts(1, 2, 0), EventRules.Count(details, Thursday));
        Assert.True(details.Responses.Single(item => item.UserId == Mira).Withdrawn);
        Assert.False(details.Responses.Single(item => item.UserId == Taro).Withdrawn);

        // Accepting again clears the withdrawal; withdrawing after confirmation keeps the date confirmed.
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Mira, id, Thursday, true));
        Assert.Equal(new EventCounts(2, 1, 0), (await fixture.Sender.Send(new ConfirmEventCommand(Guild, Organizer, id, Thursday))).Value);
        var late = (await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, false))).Value;
        Assert.True(late.Withdrawn);
        Assert.Equal(OccurrenceStatus.Confirmed, late.Status);
        details = (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, id))).Value.Details;
        Assert.Equal(OccurrenceStatus.Confirmed, details.Occurrences[0].Status);
        Assert.Equal(new EventCounts(1, 2, 0), EventRules.Count(details, Thursday));
    }

    [Fact]
    public async Task OnlyTheOrganizerConfirmsAndConfirmingTwiceNotifiesOnce()
    {
        using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Sender.Send(Create(Organizer) with { Invitees = [Kai, Mira] })).Value.EventId;
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, true));

        Assert.True((await fixture.Sender.Send(new ConfirmEventCommand(Guild, Kai, id, Thursday))).IsFailure);
        Assert.Equal(1, (await fixture.Sender.Send(new ConfirmEventCommand(Guild, Organizer, id, Thursday))).Value.Accepted);
        Assert.True((await fixture.Sender.Send(new ConfirmEventCommand(Guild, Organizer, id, Thursday))).IsSuccess);

        await fixture.Sender.Send(new DispatchEventMessagesCommand(100));
        await fixture.Sender.Send(new DispatchEventMessagesCommand(100));
        Assert.Equal([Kai], fixture.Notifier.Sent.Where(item => item.Kind == EventNotificationKind.Confirmed).Select(item => item.UserId));
        Assert.Equal(2, fixture.Notifier.Sent.Count(item => item.Kind == EventNotificationKind.Invite));
    }

    [Fact]
    public async Task CancelingADateOrTheSeriesNotifiesThoseWhoExpectedItAndOnlyTheOrganizerCan()
    {
        using var fixture = await Fixture.CreateAsync();
        var weekly = Create(Organizer) with
        {
            Invitees = [Kai, Mira, Taro], Recurrence = EventRecurrence.Weekly, Weekdays = [DayOfWeek.Thursday], Until = Thursday.AddDays(21)
        };
        var id = (await fixture.Sender.Send(weekly)).Value.EventId;
        await fixture.Sender.Send(new DispatchEventMessagesCommand(100)); // invitations for the first date only (3-day lead)
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Taro, id, Thursday, false));
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday.AddDays(7), true));

        Assert.True((await fixture.Sender.Send(new CancelEventCommand(Guild, Kai, id, Thursday))).IsFailure);
        Assert.Equal(1, (await fixture.Sender.Send(new CancelEventCommand(Guild, Organizer, id, Thursday))).Value);
        Assert.Equal(0, (await fixture.Sender.Send(new CancelEventCommand(Guild, Organizer, id, Thursday))).Value);
        await fixture.Sender.Send(new DispatchEventMessagesCommand(100));
        // Kai and Mira got the invitation and didn't decline; Taro declined.
        Assert.Equal([Kai, Mira], fixture.Notifier.Sent.Where(item => item.Kind == EventNotificationKind.Canceled).Select(item => item.UserId).Order());

        Assert.True((await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, true))).IsFailure);
        Assert.Equal(3, (await fixture.Sender.Send(new CancelEventCommand(Guild, Organizer, id, null))).Value);
        var details = (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, id))).Value.Details;
        Assert.All(details.Occurrences, item => Assert.Equal(OccurrenceStatus.Canceled, item.Status));
        Assert.NotNull(details.Event.CanceledAt);
        fixture.Notifier.Sent.Clear();
        fixture.Clock.Advance(TimeSpan.FromDays(10)); // the next dates enter the invitation window: none go out
        await fixture.Sender.Send(new DispatchEventMessagesCommand(100));
        // The series message reaches Kai, who had accepted a later date; nobody gets new invitations.
        Assert.Equal([(Kai, EventNotificationKind.Canceled)], fixture.Notifier.Sent.Select(item => (item.UserId, item.Kind)));
    }

    [Fact]
    public async Task RecurrenceProducesTheRightDatesAndKeepsTheLocalTimeAcrossDaylightSaving()
    {
        using var fixture = await Fixture.CreateAsync();
        async Task<IReadOnlyList<EventOccurrence>> Dates(CreateEventCommand command)
        {
            var result = await fixture.Sender.Send(command with { RequestId = Guid.NewGuid() });
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : null);
            return (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, result.Value.EventId))).Value.Details.Occurrences;
        }

        var daily = await Dates(Create(Organizer) with { Recurrence = EventRecurrence.Daily, Until = Thursday.AddDays(4) });
        Assert.Equal(5, daily.Count);
        var weekly = await Dates(Create(Organizer) with { Recurrence = EventRecurrence.Weekly, Weekdays = [DayOfWeek.Saturday], Until = Thursday.AddDays(14) });
        Assert.Equal([new DateOnly(2026, 11, 21), new DateOnly(2026, 11, 28)], weekly.Select(item => item.Date));
        var several = await Dates(Create(Organizer) with
        {
            Recurrence = EventRecurrence.Weekly, Weekdays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], Until = Thursday.AddDays(7)
        });
        Assert.Equal([DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday], several.Select(item => item.Date.DayOfWeek));

        // Europe/Kyiv moves to summer time on 28 March 2027: 20:00 stays 20:00 local, so the UTC hour shifts.
        var spring = await Dates(Create(Organizer) with
        {
            StartDate = new DateOnly(2027, 3, 25), Recurrence = EventRecurrence.Weekly, Weekdays = [DayOfWeek.Thursday], Until = new DateOnly(2027, 4, 1)
        });
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Kyiv);
        Assert.All(spring, item => Assert.Equal(new TimeOnly(20, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.Start, zone).DateTime)));
        Assert.Equal([18, 17], spring.Select(item => item.Start.UtcDateTime.Hour));

        Assert.True((await fixture.Sender.Send(Create(Organizer) with { RequestId = Guid.NewGuid(), Recurrence = EventRecurrence.Weekly, Weekdays = [], Until = Thursday.AddDays(7) })).IsFailure);
        Assert.True((await fixture.Sender.Send(Create(Organizer) with { RequestId = Guid.NewGuid(), Recurrence = EventRecurrence.Daily, Until = null })).IsFailure);
        Assert.True((await fixture.Sender.Send(Create(Organizer) with { RequestId = Guid.NewGuid(), StartDate = new DateOnly(2026, 11, 1) })).IsFailure);
    }

    [Fact]
    public async Task InvitationsGoOutAheadOfEachDateInSmallBatchesWithoutDuplicatesAndClosedDmsAreRecorded()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Notifier.Outcomes[Mira] = EventDeliveryOutcome.Undeliverable;
        fixture.Notifier.Outcomes[Taro] = EventDeliveryOutcome.RetryLater;
        var id = (await fixture.Sender.Send(Create(Organizer) with
        {
            Invitees = [Kai, Mira, Taro], Recurrence = EventRecurrence.Daily, Until = Thursday.AddDays(13)
        })).Value.EventId;

        Assert.Equal(2, (await fixture.Sender.Send(new DispatchEventMessagesCommand(2))).Value + 1); // Kai sent, Mira failed: batch of 2
        await fixture.Sender.Send(new DispatchEventMessagesCommand(100));
        await fixture.Sender.Send(new DispatchEventMessagesCommand(100));
        // 17 Nov now; dates within 3 days: 19 Nov only (start 19 Nov). Each invitee once.
        Assert.Equal(3, fixture.Notifier.Attempts.Count(item => item.Occurrence.Date == Thursday && item.UserId != Taro) + 1);
        var details = (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, id))).Value.Details;
        Assert.Contains(details.Deliveries, item => item.UserId == Mira && item.Failed);
        Assert.Contains(details.Deliveries, item => item.UserId == Taro && !item.Sent && !item.Failed);
        Assert.DoesNotContain(details.Deliveries, item => item.Date > Thursday);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Sender.Send(new DispatchEventMessagesCommand(100));
        Assert.Equal(2, fixture.Notifier.Attempts.Count(item => item.UserId == Taro));
        Assert.Single(fixture.Notifier.Attempts, item => item.UserId == Mira);
    }

    [Fact]
    public async Task OrganizerCanHandOverAndThenWithdrawLikeAnyInvitee()
    {
        using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Sender.Send(Create(Organizer) with { Invitees = [Kai, Mira] })).Value.EventId;

        Assert.True((await fixture.Sender.Send(new TransferEventCommand(Guild, Kai, id, Mira))).IsFailure);
        Assert.True((await fixture.Sender.Send(new TransferEventCommand(Guild, Organizer, id, Outsider))).IsFailure);
        Assert.True((await fixture.Sender.Send(new TransferEventCommand(Guild, Organizer, id, Kai))).IsSuccess);

        var view = (await fixture.Sender.Send(new GetEventQuery(Guild, Organizer, id))).Value;
        Assert.Equal(Kai, view.Details.Event.OrganizerId);
        Assert.Equal([Organizer, Mira], view.Details.Invitees.Order());
        Assert.Equal(1, EventRules.Count(view.Details, Thursday).Accepted);
        Assert.True((await fixture.Sender.Send(new ConfirmEventCommand(Guild, Organizer, id, Thursday))).IsFailure);

        var withdrawn = await fixture.Sender.Send(new RespondToEventCommand(Guild, Organizer, id, Thursday, false));
        Assert.True(withdrawn.Value.Withdrawn);
        Assert.True((await fixture.Sender.Send(new ConfirmEventCommand(Guild, Kai, id, Thursday))).IsSuccess);
    }

    [Fact]
    public async Task ConfirmedEventsShowForTheOrganizerAndAcceptedInviteesWithTitlesOnlyForParticipants()
    {
        using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Sender.Send(Create(Organizer) with { Invitees = [Kai, Mira] })).Value.EventId;
        await fixture.Sender.Send(new RespondToEventCommand(Guild, Kai, id, Thursday, true));
        var from = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        var query = new GetMembersEventsQuery(Guild, Mira, [Organizer, Kai, Mira, Taro], from, from.AddDays(3));

        Assert.Empty((await fixture.Sender.Send(query)).Value); // pending dates don't count
        await fixture.Sender.Send(new ConfirmEventCommand(Guild, Organizer, id, Thursday));

        var blocks = (await fixture.Sender.Send(query)).Value;
        Assert.Equal([Organizer, Kai], blocks.Select(item => item.UserId).Order());
        Assert.All(blocks, item => Assert.True(item.Visible));
        Assert.All((await fixture.Sender.Send(query with { ViewerId = Taro })).Value, item => Assert.False(item.Visible));
    }

    private static CreateEventCommand Create(ulong organizer) => new(Guild, organizer, Guid.NewGuid(), null, [Kai],
        "Thursday scrims", "Bring a mic", Thursday, "20:00", "23:00", Kyiv, "UTC", EventRecurrence.Once, [], null);

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public MutableClock Clock { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public ISender Sender => application.GetRequiredService<ISender>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton(storage.GetRequiredService<IEventStore>())
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton<IEventNotifier>(fixture.Notifier)
                .AddSingleton<IGroupRoleGateway, NoRoles>()
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
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class RecordingNotifier : IEventNotifier
    {
        public Dictionary<ulong, EventDeliveryOutcome> Outcomes { get; } = [];
        public List<EventOutboxItem> Attempts { get; } = [];
        public List<EventOutboxItem> Sent { get; } = [];

        public Task<EventDeliveryOutcome> SendAsync(EventOutboxItem item, CancellationToken cancellationToken)
        {
            Attempts.Add(item);
            var outcome = Outcomes.GetValueOrDefault(item.UserId, EventDeliveryOutcome.Sent);
            if (outcome == EventDeliveryOutcome.Sent)
                Sent.Add(item);
            return Task.FromResult(outcome);
        }
    }

    private sealed class NoRoles : IGroupRoleGateway
    {
        public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) => Task.FromResult<IReadOnlyCollection<ulong>>([]);
        public Task<ulong> CreateAsync(ulong guildId, string name) => Task.FromResult(0UL);
        public Task DeleteAsync(ulong guildId, ulong roleId) => Task.CompletedTask;
        public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta) => Task.CompletedTask;
    }
}
