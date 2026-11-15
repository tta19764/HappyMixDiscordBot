using System.Globalization;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Events;

/// <summary>
/// Creates an event. <paramref name="RequestId"/> comes from the form, so submitting it twice creates one event.
/// A group event invites every current group member; a standalone event invites exactly <paramref name="Invitees"/>.
/// <paramref name="End"/> at or before <paramref name="Start"/> runs into the next day.
/// </summary>
public sealed record CreateEventCommand(ulong GuildId, ulong UserId, Guid RequestId, Guid? GroupId, IReadOnlyList<ulong> Invitees,
    string Title, string? Description, DateOnly StartDate, string Start, string End, string? TimeZone, string DefaultTimeZone,
    EventRecurrence Recurrence, IReadOnlyList<DayOfWeek> Weekdays, DateOnly? Until) : ICommand<EventCreated>;

public sealed record EventCreated(Guid EventId, int Invited, int Dates, bool AlreadyExisted);

/// <summary>Accepts or declines one date. Declining after accepting is a withdrawal.</summary>
public sealed record RespondToEventCommand(ulong GuildId, ulong UserId, Guid EventId, DateOnly Date, bool Accept) : ICommand<EventAnswer>;

public sealed record EventAnswer(string Title, DateTimeOffset Start, string TimeZone, OccurrenceStatus Status, bool Accepted, bool Withdrawn, bool Changed);

/// <summary>The organizer confirms one date; everyone who accepted gets a Discord update.</summary>
public sealed record ConfirmEventCommand(ulong GuildId, ulong UserId, Guid EventId, DateOnly Date) : ICommand<EventCounts>;

/// <summary>The organizer cancels one date, or the whole series when <paramref name="Date"/> is null.</summary>
public sealed record CancelEventCommand(ulong GuildId, ulong UserId, Guid EventId, DateOnly? Date) : ICommand<int>;

public sealed record EventCounts(int Accepted, int Declined, int Waiting);

public sealed record EventView(EventDetails Details, ulong ViewerId, bool IsOrganizer, bool IsInvitee);

/// <summary>An event the viewer may see: organizer, invitee, or a member of its group.</summary>
public sealed record GetEventQuery(ulong GuildId, ulong UserId, Guid EventId) : IQuery<EventView>;

/// <summary>Events the user organizes or is invited to that haven't ended.</summary>
public sealed record GetMemberEventsQuery(ulong GuildId, ulong UserId) : IQuery<MemberEvents>;

public sealed record MemberEvents(ulong ViewerId, IReadOnlyList<EventDetails> Events);

public sealed record GetGroupEventsQuery(ulong GuildId, Guid GroupId) : IQuery<IReadOnlyList<EventDetails>>;

/// <summary>Groups the user belongs to, for "plan an event for a group".</summary>
public sealed record GetEventGroupsQuery(ulong GuildId, ulong UserId) : IQuery<IReadOnlyList<EventGroupOption>>;

public sealed record EventGroupOption(Guid Id, string Name, int Members);

/// <summary>Queues invitations for dates coming up and sends a batch of waiting Discord messages.</summary>
public sealed record DispatchEventMessagesCommand(int Limit = 20) : ICommand<int>;

public static class EventRules
{
    public const int MaxInvitees = 50;
    public const int TitleLength = 80;
    public const int DescriptionLength = 1000;
    /// <summary>Invitations go out this long before each date, so a long series doesn't send everything at once.</summary>
    public static readonly TimeSpan InviteLead = TimeSpan.FromDays(3);

    public static EventCounts Count(EventDetails details, DateOnly date)
    {
        var answers = details.Responses.Where(item => item.Date == date && details.Invitees.Contains(item.UserId)).ToArray();
        int accepted = answers.Count(item => item.Accepted), declined = answers.Count(item => !item.Accepted);
        return new EventCounts(accepted, declined, details.Invitees.Count - accepted - declined);
    }
}

internal sealed class EventHandlers(IEventStore store, IGuildGroupStore groups, IAccountLinkStore accountLinks,
    IGuildSettingsStore settings, IClock clock, IEventNotifier notifier) :
    ICommandHandler<CreateEventCommand, EventCreated>,
    ICommandHandler<RespondToEventCommand, EventAnswer>,
    ICommandHandler<ConfirmEventCommand, EventCounts>,
    ICommandHandler<CancelEventCommand, int>,
    IQueryHandler<GetEventQuery, EventView>,
    IQueryHandler<GetMemberEventsQuery, MemberEvents>,
    IQueryHandler<GetGroupEventsQuery, IReadOnlyList<EventDetails>>,
    IQueryHandler<GetEventGroupsQuery, IReadOnlyList<EventGroupOption>>,
    ICommandHandler<DispatchEventMessagesCommand, int>
{
    public async Task<Result<IReadOnlyList<EventGroupOption>>> Handle(GetEventGroupsQuery query, CancellationToken cancellationToken)
    {
        var links = await accountLinks.LoadAsync(query.GuildId);
        ulong viewer = links.Member(query.UserId);
        return Result.Success<IReadOnlyList<EventGroupOption>>((await groups.ListAsync(query.GuildId))
            .Select(group => (Group: group, Members: group.Members.Append(group.OrganizerId).Select(links.Member).ToHashSet()))
            .Where(item => item.Members.Contains(viewer))
            .Select(item => new EventGroupOption(item.Group.Id, item.Group.Name, item.Members.Count))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    public Task<Result<EventCreated>> Handle(CreateEventCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        string title = command.Title.Trim();
        string? description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        if (title.Length is 0 or > EventRules.TitleLength)
            throw new EventRuleException("Give the event a title of up to 80 characters.");
        if (description is { Length: > EventRules.DescriptionLength })
            throw new EventRuleException("Keep the description under 1000 characters.");

        var zone = await ZoneAsync(command.GuildId, command.TimeZone, command.DefaultTimeZone, cancellationToken);
        var (start, end) = (ParseTime(command.Start), ParseTime(command.End));
        int duration = (int)((end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes + 1440) % 1440;
        if (duration == 0)
            throw new EventRuleException("The end time must differ from the start time.");

        var weekdays = command.Recurrence == EventRecurrence.Weekly ? command.Weekdays.Distinct().Order().ToArray() : [];
        DateOnly? until = command.Recurrence == EventRecurrence.Once ? null : command.Until;
        if (command.Recurrence == EventRecurrence.Weekly && weekdays.Length == 0)
            throw new EventRuleException("Pick at least one weekday for a weekly event.");
        if (command.Recurrence != EventRecurrence.Once)
        {
            if (until is null || until < command.StartDate)
                throw new EventRuleException("Choose an end date on or after the first date.");
            if (until.Value.DayNumber - command.StartDate.DayNumber > EventSchedule.MaxSeriesDays)
                throw new EventRuleException("A repeating event can run for up to 26 weeks.");
        }

        var links = await accountLinks.LoadAsync(command.GuildId);
        ulong organizer = links.Member(command.UserId);
        IEnumerable<ulong> invited;
        if (command.GroupId is { } groupId)
        {
            var group = (await groups.ListAsync(command.GuildId)).FirstOrDefault(item => item.Id == groupId)
                ?? throw new EventRuleException("That group no longer exists.");
            var members = group.Members.Append(group.OrganizerId).Select(links.Member).ToHashSet();
            if (!members.Contains(organizer))
                throw new EventRuleException("Only members of the group can plan an event for it.");
            invited = members;
        }
        else
            invited = command.Invitees.Select(links.Member);

        var invitees = invited.Where(id => id != organizer).Distinct().ToArray();
        if (invitees.Length == 0)
            throw new EventRuleException(command.GroupId is null ? "Invite at least one person." : "The group has no other members to invite yet.");
        if (invitees.Length > EventRules.MaxInvitees)
            throw new EventRuleException("Invite up to 50 people.");

        var guildEvent = new GuildEvent(command.RequestId, command.GuildId, organizer, command.GroupId, title, description, zone.Id,
            command.StartDate, start, duration, command.Recurrence, weekdays, until, clock.UtcNow, null);
        var occurrences = EventSchedule.Occurrences(guildEvent, zone).Where(item => item.Start > clock.UtcNow).ToArray();
        if (occurrences.Length == 0)
            throw new EventRuleException(command.Recurrence == EventRecurrence.Once
                ? "That time has already passed. Pick a time in the future."
                : "No date of this schedule is in the future. Check the days and dates.");
        if (occurrences.Length > EventSchedule.MaxOccurrences)
            throw new EventRuleException("That schedule has too many dates. Use fewer weekdays or an earlier end date.");

        bool created = await store.CreateAsync(guildEvent, invitees, occurrences);
        if (created)
            await store.EnqueueDueInvitesAsync(clock.UtcNow, clock.UtcNow + EventRules.InviteLead);
        return new EventCreated(guildEvent.Id, invitees.Length, occurrences.Length, !created);
    });

    public Task<Result<EventAnswer>> Handle(RespondToEventCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var (details, viewer) = await LoadAsync(command.GuildId, command.UserId, command.EventId);
        if (!details.Invitees.Contains(viewer))
            throw new EventRuleException("You aren't invited to this event.");
        var occurrence = Occurrence(details, command.Date);
        if (occurrence.Status == OccurrenceStatus.Canceled)
            throw new EventRuleException("This date was canceled.");
        if (occurrence.End <= clock.UtcNow)
            throw new EventRuleException("This date is over.");

        var change = await store.RespondAsync(command.EventId, command.Date, viewer, command.Accept, clock.UtcNow);
        return new EventAnswer(details.Event.Title, occurrence.Start, details.Event.TimeZone, occurrence.Status, change.Accepted,
            change.Withdrawn, change.PreviouslyAccepted != change.Accepted);
    });

    public Task<Result<EventCounts>> Handle(ConfirmEventCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var (details, viewer) = await LoadAsync(command.GuildId, command.UserId, command.EventId);
        RequireOrganizer(details, viewer);
        var occurrence = Occurrence(details, command.Date);
        if (occurrence.Status == OccurrenceStatus.Canceled)
            throw new EventRuleException("This date was canceled.");
        if (occurrence.End <= clock.UtcNow)
            throw new EventRuleException("This date is over.");

        // Confirming twice is harmless: the status only moves once and each person gets at most one update per date.
        await store.SetStatusAsync(command.EventId, command.Date, [OccurrenceStatus.Pending], OccurrenceStatus.Confirmed, clock.UtcNow);
        var accepted = details.Responses.Where(item => item.Date == command.Date && item.Accepted && details.Invitees.Contains(item.UserId));
        await store.EnqueueAsync(accepted.Select(item => (command.EventId, command.Date, item.UserId, EventNotificationKind.Confirmed)), clock.UtcNow);
        return EventRules.Count(details, command.Date);
    });

    public Task<Result<int>> Handle(CancelEventCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var (details, viewer) = await LoadAsync(command.GuildId, command.UserId, command.EventId);
        RequireOrganizer(details, viewer);
        IReadOnlyList<DateOnly> canceled;
        if (command.Date is { } date)
        {
            var occurrence = Occurrence(details, date);
            if (occurrence.Start <= clock.UtcNow && occurrence.Status != OccurrenceStatus.Canceled)
                throw new EventRuleException("This date has already started.");
            canceled = await store.SetStatusAsync(command.EventId, date, [OccurrenceStatus.Pending, OccurrenceStatus.Confirmed],
                OccurrenceStatus.Canceled, clock.UtcNow) ? [date] : [];
        }
        else
            canceled = await store.CancelSeriesAsync(command.EventId, clock.UtcNow);

        if (canceled.Count == 0)
            return 0;

        // Tell the people who were expecting these dates: those who accepted or already got an invitation and didn't
        // decline. A series sends one message per person, keyed on its first canceled date.
        var notify = details.Invitees.Select(user => (User: user, Dates: canceled.Where(day =>
            details.Responses.Any(item => item.UserId == user && item.Date == day && item.Accepted) ||
            (details.Deliveries.Any(item => item.UserId == user && item.Date == day && item.Kind == EventNotificationKind.Invite && item.Sent) &&
             !details.Responses.Any(item => item.UserId == user && item.Date == day && !item.Accepted))).ToArray()))
            .Where(item => item.Dates.Length > 0)
            .Select(item => (command.EventId, command.Date ?? item.Dates[0], item.User, EventNotificationKind.Canceled));
        await store.EnqueueAsync(notify, clock.UtcNow);
        return canceled.Count;
    });

    public Task<Result<EventView>> Handle(GetEventQuery query, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var (details, viewer) = await LoadAsync(query.GuildId, query.UserId, query.EventId);
        bool organizer = details.Event.OrganizerId == viewer, invitee = details.Invitees.Contains(viewer);
        if (!organizer && !invitee)
        {
            var group = details.Event.GroupId is { } groupId ? (await groups.ListAsync(query.GuildId)).FirstOrDefault(item => item.Id == groupId) : null;
            var links = await accountLinks.LoadAsync(query.GuildId);
            if (group is null || !group.Members.Append(group.OrganizerId).Select(links.Member).Contains(viewer))
                throw new EventRuleException("This event isn't available to you.");
        }
        return new EventView(details, viewer, organizer, invitee);
    });

    public async Task<Result<MemberEvents>> Handle(GetMemberEventsQuery query, CancellationToken cancellationToken)
    {
        ulong viewer = (await accountLinks.LoadAsync(query.GuildId)).Member(query.UserId);
        return Result.Success(new MemberEvents(viewer, await store.ListForUserAsync(query.GuildId, viewer, clock.UtcNow)));
    }

    public async Task<Result<IReadOnlyList<EventDetails>>> Handle(GetGroupEventsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.ListForGroupAsync(query.GuildId, query.GroupId, clock.UtcNow));

    public async Task<Result<int>> Handle(DispatchEventMessagesCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await store.EnqueueDueInvitesAsync(now, now + EventRules.InviteLead);
        int sent = 0;
        foreach (var item in await store.DueMessagesAsync(now, command.Limit))
        {
            // A date canceled after its invitation was queued no longer needs it; a canceled date gets no confirmation.
            bool stale = item.Kind != EventNotificationKind.Canceled &&
                (item.Occurrence.Status == OccurrenceStatus.Canceled || item.Event.CanceledAt is not null || item.Occurrence.End <= now);
            if (stale)
            {
                await store.MarkAsync(item.Event.Id, item.Occurrence.Date, item.UserId, item.Kind, false, "No longer needed", null);
                continue;
            }

            var outcome = await notifier.SendAsync(item, cancellationToken);
            switch (outcome)
            {
                case EventDeliveryOutcome.Sent:
                    sent++;
                    await store.MarkAsync(item.Event.Id, item.Occurrence.Date, item.UserId, item.Kind, true, null, null);
                    break;
                case EventDeliveryOutcome.RetryLater when item.Attempts < 3:
                    await store.MarkAsync(item.Event.Id, item.Occurrence.Date, item.UserId, item.Kind, false, "Discord was unavailable",
                        now.AddMinutes(Math.Pow(4, item.Attempts + 1)));
                    break;
                default:
                    await store.MarkAsync(item.Event.Id, item.Occurrence.Date, item.UserId, item.Kind, false,
                        outcome == EventDeliveryOutcome.Undeliverable ? "Direct messages are closed" : "Discord was unavailable", null);
                    break;
            }
        }
        return Result.Success(sent);
    }

    private async Task<(EventDetails Details, ulong Viewer)> LoadAsync(ulong guildId, ulong userId, Guid eventId)
    {
        var details = await store.GetAsync(guildId, eventId) ?? throw new EventRuleException("That event doesn't exist.");
        return (details, (await accountLinks.LoadAsync(guildId)).Member(userId));
    }

    private static EventOccurrence Occurrence(EventDetails details, DateOnly date) =>
        details.Occurrences.FirstOrDefault(item => item.Date == date) ?? throw new EventRuleException("That date isn't part of this event.");

    private static void RequireOrganizer(EventDetails details, ulong viewer)
    {
        if (details.Event.OrganizerId != viewer)
            throw new EventRuleException("Only the organizer can do that.");
    }

    private async Task<TimeZoneInfo> ZoneAsync(ulong guildId, string? requested, string defaultZone, CancellationToken cancellationToken)
    {
        // Events need a named zone (not a fixed offset) so the local start time survives daylight-saving changes.
        if (!string.IsNullOrWhiteSpace(requested))
            return LocalTimes.FindZone(requested.Trim()) ?? throw new EventRuleException("Choose a time zone like Europe/Kyiv.");
        var saved = await settings.GetAsync(guildId, cancellationToken);
        return LocalTimes.FindZone(saved?.TimeZoneId ?? defaultZone) ?? TimeZoneInfo.Utc;
    }

    private static TimeOnly ParseTime(string value) =>
        TimeOnly.TryParseExact(value.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time : throw new EventRuleException("Use times like 19:30.");

    private static async Task<Result<T>> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Result.Success(await action());
        }
        catch (EventRuleException exception)
        {
            return Result.Failure<T>(new("Event.Rule", exception.Message));
        }
    }

    private sealed class EventRuleException(string message) : Exception(message);
}
