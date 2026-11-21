using System.Globalization;
using System.Text.Json;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Live;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Quiet;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Quiet;

/// <summary>Kinds of personal messages that wait out do-not-disturb, and their payloads.</summary>
public static class HeldKinds
{
    public const string Live = "live";
    public const string Group = "group";
    public const string GroupInvite = "group-invite";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>A group invitation's Accept/Decline message, kept until do-not-disturb ends.</summary>
public sealed record HeldGroupInvite(ulong GuildId, string GuildName, string GroupName, ulong UserId, Guid Token);

/// <summary>The viewer's do-not-disturb ranges, whether one is on now (until when) and how many messages wait.</summary>
public sealed record QuietOverview(QuietSchedule Schedule, DateTimeOffset? QuietUntil, int Held);

public sealed record GetQuietOverviewQuery(ulong UserId) : IQuery<QuietOverview>;

/// <summary>Adds the same hours on each chosen weekday. Equal start and end means the whole day.</summary>
public sealed record AddQuietWeeklyCommand(ulong UserId, IReadOnlyCollection<DayOfWeek> Days, string Start, string End, bool FullDay, string TimeZone)
    : ICommand<int>;

/// <summary>Adds do-not-disturb on every date from <paramref name="First"/> to <paramref name="Last"/>.</summary>
public sealed record AddQuietDatesCommand(ulong UserId, DateOnly First, DateOnly Last, string Start, string End, bool FullDay, string TimeZone)
    : ICommand<int>;

public sealed record RemoveQuietCommand(ulong UserId, Guid Id) : ICommand;

/// <summary>When the person's current do-not-disturb ends, or null when they can be messaged now.</summary>
public sealed record GetQuietUntilQuery(ulong UserId) : IQuery<DateTimeOffset?>;

/// <summary>
/// Holds a personal message when the recipient has do-not-disturb on. Returns true when it was held, false when it
/// can be sent now. <paramref name="Key"/> collapses repeats of the same message.
/// </summary>
public sealed record HoldIfQuietCommand(ulong UserId, string Kind, string? Key, string Payload) : ICommand<bool>;

/// <summary>Sends held messages whose do-not-disturb is over, after checking they still matter. Returns how many went out.</summary>
public sealed record ReleaseHeldMessagesCommand(int Limit = 50) : ICommand<int>;

public static class QuietRules
{
    public const int MaxRanges = 40;
    public const int MaxDays = 62;
    /// <summary>A message held longer than this is dropped rather than delivered stale.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromDays(7);
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);
}

/// <summary>Turns do-not-disturb ranges into instants.</summary>
public static class QuietHours
{
    /// <summary>Do-not-disturb stretches that overlap [from, to), merged and in order.</summary>
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Intervals(QuietSchedule schedule, DateTimeOffset from, DateTimeOffset to)
    {
        var items = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        items.AddRange(schedule.Dates.Where(range => range.Start < to && from < range.End).Select(range => (range.Start, range.End)));
        foreach (var weekly in schedule.Weekly)
        {
            var asAvailability = new WeeklyAvailability(weekly.Day, weekly.Start, weekly.End, weekly.TimeZone);
            var zone = LocalTimes.FindZone(weekly.TimeZone);
            if (zone is null)
                continue;
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
            // ForDate looks two days either side, so stepping a few days at a time covers the span without gaps.
            for (var day = first; day <= last.AddDays(2); day = day.AddDays(4))
                items.AddRange(WeeklyOccurrences.ForDate(asAvailability, day).Where(item => item.Start < to && from < item.End));
        }

        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var item in items.Distinct().OrderBy(item => item.Start))
        {
            if (merged.Count > 0 && item.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, item.End > merged[^1].End ? item.End : merged[^1].End);
            else
                merged.Add(item);
        }
        return merged;
    }

    /// <summary>When the do-not-disturb on at <paramref name="now"/> ends, following back-to-back ranges; null when none is on.</summary>
    public static DateTimeOffset? Until(QuietSchedule schedule, DateTimeOffset now)
    {
        if (schedule.IsEmpty)
            return null;
        var stretches = Intervals(schedule, now, now.AddDays(QuietRules.MaxHold.TotalDays));
        return stretches.Count > 0 && stretches[0].Start <= now && stretches[0].End > now ? stretches[0].End : null;
    }
}

internal sealed class QuietHandlers(IQuietStore store, IClock clock, IGuildGroupStore groups, ILiveStore live,
    IEnumerable<IHeldMessageSender> senders) :
    IQueryHandler<GetQuietOverviewQuery, QuietOverview>, ICommandHandler<AddQuietWeeklyCommand, int>, ICommandHandler<AddQuietDatesCommand, int>,
    ICommandHandler<RemoveQuietCommand>, IQueryHandler<GetQuietUntilQuery, DateTimeOffset?>, ICommandHandler<HoldIfQuietCommand, bool>,
    ICommandHandler<ReleaseHeldMessagesCommand, int>
{
    private static readonly ErrorDescriptor TooMany = new("Quiet.TooMany", "You already have the most do-not-disturb times allowed. Remove one first.");

    public async Task<Result<QuietOverview>> Handle(GetQuietOverviewQuery query, CancellationToken cancellationToken)
    {
        var schedule = await store.GetAsync(query.UserId);
        return new QuietOverview(schedule, QuietHours.Until(schedule, clock.UtcNow), await store.CountHeldAsync(query.UserId));
    }

    public async Task<Result<int>> Handle(AddQuietWeeklyCommand command, CancellationToken cancellationToken)
    {
        if (command.Days.Count == 0 || command.Days.Any(day => !Enum.IsDefined(day)))
            return Result.Failure<int>(Invalid("Choose at least one day."));
        if (LocalTimes.FindZone(command.TimeZone) is null)
            return Result.Failure<int>(Invalid("Choose a time zone like Europe/Kyiv or enter a UTC offset like +02:00."));
        if (Times(command.Start, command.End, command.FullDay) is not var (start, end))
            return Result.Failure<int>(Invalid("Enter 24-hour times like 17 or 00:30."));
        var saved = await store.GetAsync(command.UserId);
        var days = command.Days.Distinct().ToArray();
        if (saved.Weekly.Count + saved.Dates.Count + days.Length > QuietRules.MaxRanges)
            return Result.Failure<int>(TooMany);
        await store.AddWeeklyAsync(command.UserId, days.Select(day => new QuietWeekly(Guid.NewGuid(), day, start, end, command.TimeZone)).ToArray());
        return days.Length;
    }

    public async Task<Result<int>> Handle(AddQuietDatesCommand command, CancellationToken cancellationToken)
    {
        if (command.Last < command.First || command.Last.DayNumber - command.First.DayNumber >= QuietRules.MaxDays)
            return Result.Failure<int>(Invalid("Choose an end date on or after the start date, at most 62 days later."));
        if (LocalTimes.FindZone(command.TimeZone) is not { } zone)
            return Result.Failure<int>(Invalid("Choose a time zone like Europe/Kyiv or enter a UTC offset like +02:00."));
        if (Times(command.Start, command.End, command.FullDay) is not var (start, end))
            return Result.Failure<int>(Invalid("Enter 24-hour times like 17 or 00:30."));

        // Whole days become one stretch; set hours repeat on each date and may run past midnight.
        var ranges = command.FullDay
            ? [new QuietDates(Guid.NewGuid(), LocalTimes.StartOfDay(command.First, zone), LocalTimes.StartOfDay(command.Last.AddDays(1), zone))]
            : Enumerable.Range(0, command.Last.DayNumber - command.First.DayNumber + 1).Select(command.First.AddDays).Select(day =>
                new QuietDates(Guid.NewGuid(), LocalTimes.ToInstant(day.ToDateTime(start), zone),
                    LocalTimes.ToInstant((end <= start ? day.AddDays(1) : day).ToDateTime(end), zone))).ToArray();
        if (ranges[^1].End <= clock.UtcNow)
            return Result.Failure<int>(Invalid("That time has already passed."));
        var saved = await store.GetAsync(command.UserId);
        if (saved.Weekly.Count + saved.Dates.Count + ranges.Length > QuietRules.MaxRanges)
            return Result.Failure<int>(TooMany);
        await store.AddDatesAsync(command.UserId, ranges);
        return ranges.Length;
    }

    public async Task<Result> Handle(RemoveQuietCommand command, CancellationToken cancellationToken) =>
        await store.RemoveAsync(command.UserId, command.Id) ? Result.Success() : Result.Failure(new("Quiet.NotFound", "That do-not-disturb time no longer exists."));

    public async Task<Result<DateTimeOffset?>> Handle(GetQuietUntilQuery query, CancellationToken cancellationToken) =>
        QuietHours.Until(await store.GetAsync(query.UserId), clock.UtcNow);

    public async Task<Result<bool>> Handle(HoldIfQuietCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (QuietHours.Until(await store.GetAsync(command.UserId), now) is not { } until)
            return false;
        await store.HoldAsync(command.UserId, command.Kind, command.Key, command.Payload, now, until);
        return true;
    }

    public async Task<Result<int>> Handle(ReleaseHeldMessagesCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        int sent = 0;
        foreach (var message in await store.DueAsync(now, command.Limit))
        {
            // Do-not-disturb can be extended while a message waits: hold it again until the new end.
            if (QuietHours.Until(await store.GetAsync(message.UserId), now) is { } until)
            {
                await store.RescheduleAsync(message.Id, until);
                continue;
            }
            if (now - message.HeldAt > QuietRules.MaxHold || !await StillRelevantAsync(message)
                || senders.FirstOrDefault(item => item.Kind == message.Kind) is not { } sender)
            {
                await store.RemoveHeldAsync(message.Id);
                continue;
            }
            switch (await sender.SendAsync(message, cancellationToken))
            {
                case HeldDelivery.RetryLater:
                    await store.RescheduleAsync(message.Id, now + QuietRules.Retry);
                    break;
                case HeldDelivery.Sent:
                    sent++;
                    await store.RemoveHeldAsync(message.Id);
                    break;
                default:
                    await store.RemoveHeldAsync(message.Id);
                    break;
            }
        }
        await store.PruneDatesAsync(now.AddDays(-1));
        return sent;
    }

    /// <summary>Whether a held message still says something true: the request is still open, the player still plays, and so on.</summary>
    internal async Task<bool> StillRelevantAsync(HeldMessage message)
    {
        try
        {
            switch (message.Kind)
            {
                case HeldKinds.Live:
                {
                    var notice = JsonSerializer.Deserialize<LiveNotice>(message.Payload, HeldKinds.Json);
                    if (notice is null)
                        return false;
                    var playing = await live.GetOpenAsync(notice.PlayerId, "discord") ?? await live.GetOpenAsync(notice.PlayerId, "steam");
                    var members = await live.ListMembershipsAsync(notice.GuildId, notice.GroupId);
                    return playing is not null && string.Equals(playing.Game, notice.Game, StringComparison.OrdinalIgnoreCase)
                        && members.Any(item => item.UserId == notice.PlayerId && item.Share)
                        && members.Any(item => item.UserId == message.UserId && item.Notify);
                }
                case HeldKinds.Group:
                {
                    var notice = JsonSerializer.Deserialize<GroupNotice>(message.Payload, HeldKinds.Json);
                    if (notice is null || await GroupAsync(notice.GuildId, notice.GroupId) is not { } group)
                        return false;
                    return notice.Kind switch
                    {
                        GroupNoticeKind.JoinRequested => group.JoinRequests?.Contains(notice.ActorId) == true,
                        GroupNoticeKind.JoinApproved => group.Members.Contains(message.UserId),
                        GroupNoticeKind.JoinDeclined => !group.Members.Contains(message.UserId),
                        GroupNoticeKind.GameSuggested => group.Suggestions?.Any(item => item.Id == notice.SuggestionId) == true,
                        _ => true
                    };
                }
                case HeldKinds.GroupInvite:
                {
                    var invite = JsonSerializer.Deserialize<HeldGroupInvite>(message.Payload, HeldKinds.Json);
                    return invite is not null && (await groups.ListAsync(invite.GuildId))
                        .Any(group => group.InviteTokens?.GetValueOrDefault(invite.UserId) == invite.Token);
                }
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<GuildGroup?> GroupAsync(ulong guildId, Guid groupId) =>
        (await groups.ListAsync(guildId)).FirstOrDefault(group => group.Id == groupId);

    private static (TimeOnly Start, TimeOnly End)? Times(string start, string end, bool fullDay)
    {
        if (fullDay)
            return (TimeOnly.MinValue, TimeOnly.MinValue);
        string[] formats = ["%H", "H:mm", "HH:mm"];
        return TimeOnly.TryParseExact(start.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            && TimeOnly.TryParseExact(end.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until) && from != until
                ? (from, until) : null;
    }

    private static ErrorDescriptor Invalid(string message) => new("Quiet.Invalid", message);
}
