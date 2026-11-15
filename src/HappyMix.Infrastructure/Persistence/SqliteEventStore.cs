using System.Globalization;
using HappyMix.Application.Abstractions.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>
/// Events, their dates, invitees, answers and the outbox of Discord messages. Every write is idempotent: events are
/// keyed by the ID the dashboard generated for the form, answers by person and date, and messages by person, date and
/// kind, so a repeated request or a retried delivery never creates a second row.
/// </summary>
internal sealed class SqliteEventStore(IDbContextFactory<HappyMixDbContext> factory) : IEventStore
{
    private const string DateFormat = "yyyy-MM-dd";

    public async Task<bool> CreateAsync(GuildEvent guildEvent, IReadOnlyCollection<ulong> invitees, IReadOnlyCollection<EventOccurrence> occurrences)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        if (await context.Set<EventRecord>().AnyAsync(row => row.Id == guildEvent.Id))
            return false;

        context.Add(ToRecord(guildEvent));
        context.AddRange(invitees.Distinct().Select(user => new EventInviteeRecord { EventId = guildEvent.Id, UserId = Id(user) }));
        context.AddRange(occurrences.Select(item => new EventOccurrenceRecord
        {
            EventId = guildEvent.Id, Date = Date(item.Date), StartUtc = item.Start.ToUnixTimeSeconds(), EndUtc = item.End.ToUnixTimeSeconds(),
            Status = (int)item.Status
        }));
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return false; // A concurrent submit of the same form won the race.
        }
        await transaction.CommitAsync();
        return true;
    }

    public async Task<EventDetails?> GetAsync(ulong guildId, Guid eventId)
    {
        await using var context = await factory.CreateDbContextAsync();
        var row = await context.Set<EventRecord>().FirstOrDefaultAsync(item => item.Id == eventId && item.GuildId == Id(guildId));
        return row is null ? null : (await LoadAsync(context, [row], null)).Single();
    }

    public async Task<IReadOnlyList<EventDetails>> ListForUserAsync(ulong guildId, ulong userId, DateTimeOffset from)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = Id(guildId), user = Id(userId);
        long since = from.ToUnixTimeSeconds();
        var invited = context.Set<EventInviteeRecord>().Where(item => item.UserId == user).Select(item => item.EventId);
        var active = context.Set<EventOccurrenceRecord>().Where(item => item.EndUtc >= since).Select(item => item.EventId);
        var rows = await context.Set<EventRecord>()
            .Where(item => item.GuildId == guild && (item.OrganizerId == user || invited.Contains(item.Id)) && active.Contains(item.Id))
            .ToListAsync();
        return await LoadAsync(context, rows, since);
    }

    public async Task<IReadOnlyList<EventDetails>> ListForGroupAsync(ulong guildId, Guid groupId, DateTimeOffset from)
    {
        await using var context = await factory.CreateDbContextAsync();
        long since = from.ToUnixTimeSeconds();
        var active = context.Set<EventOccurrenceRecord>().Where(item => item.EndUtc >= since).Select(item => item.EventId);
        var rows = await context.Set<EventRecord>()
            .Where(item => item.GuildId == Id(guildId) && item.GroupId == groupId && active.Contains(item.Id)).ToListAsync();
        return await LoadAsync(context, rows, since);
    }

    public async Task<EventResponseChange> RespondAsync(Guid eventId, DateOnly day, ulong userId, bool accepted, DateTimeOffset now)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var row = await context.Set<EventResponseRecord>().FindAsync(eventId, Date(day), Id(userId));
        bool? previous = row?.Accepted;
        if (row is null)
            context.Add(row = new EventResponseRecord { EventId = eventId, Date = Date(day), UserId = Id(userId) });
        if (previous != accepted)
        {
            // Declining after accepting is a withdrawal; accepting again clears it.
            row.Withdrawn = previous == true && !accepted;
            row.Accepted = accepted;
            row.RespondedUtc = now.ToUnixTimeSeconds();
        }
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return new EventResponseChange(previous, accepted, row.Withdrawn);
    }

    public async Task<bool> SetStatusAsync(Guid eventId, DateOnly day, IReadOnlyCollection<OccurrenceStatus> from, OccurrenceStatus target, DateTimeOffset now)
    {
        await using var context = await factory.CreateDbContextAsync();
        var allowed = from.Select(status => (int)status).ToArray();
        long stamp = now.ToUnixTimeSeconds();
        int changed = await context.Set<EventOccurrenceRecord>()
            .Where(item => item.EventId == eventId && item.Date == Date(day) && allowed.Contains(item.Status))
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, (int)target)
                .SetProperty(item => item.ConfirmedUtc, item => target == OccurrenceStatus.Confirmed ? stamp : item.ConfirmedUtc)
                .SetProperty(item => item.CanceledUtc, item => target == OccurrenceStatus.Canceled ? stamp : item.CanceledUtc));
        return changed == 1;
    }

    public async Task<IReadOnlyList<DateOnly>> CancelSeriesAsync(Guid eventId, DateTimeOffset now)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        long stamp = now.ToUnixTimeSeconds();
        var rows = await context.Set<EventOccurrenceRecord>()
            .Where(item => item.EventId == eventId && item.StartUtc > stamp && item.Status != (int)OccurrenceStatus.Canceled).ToListAsync();
        foreach (var row in rows)
        {
            row.Status = (int)OccurrenceStatus.Canceled;
            row.CanceledUtc = stamp;
        }
        await context.Set<EventRecord>().Where(item => item.Id == eventId && item.CanceledUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanceledUtc, stamp));
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return rows.Select(row => ParseDate(row.Date)).ToArray();
    }

    public async Task<bool> TransferOrganizerAsync(Guid eventId, ulong current, ulong successor, IReadOnlyCollection<DateOnly> openDates, DateTimeOffset now)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var row = await context.Set<EventRecord>().FirstOrDefaultAsync(item => item.Id == eventId && item.OrganizerId == Id(current));
        var invitee = await context.Set<EventInviteeRecord>().FindAsync(eventId, Id(successor));
        if (row is null || invitee is null)
            return false;
        row.OrganizerId = Id(successor);
        context.Remove(invitee);
        context.Add(new EventInviteeRecord { EventId = eventId, UserId = Id(current) });
        long stamp = now.ToUnixTimeSeconds();
        foreach (var day in openDates)
        {
            var answer = await context.Set<EventResponseRecord>().FindAsync(eventId, Date(day), Id(current));
            if (answer is null)
                context.Add(new EventResponseRecord { EventId = eventId, Date = Date(day), UserId = Id(current), Accepted = true, RespondedUtc = stamp });
            else
            {
                answer.Accepted = true;
                answer.Withdrawn = false;
                answer.RespondedUtc = stamp;
            }
        }
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task EnqueueAsync(IEnumerable<(Guid EventId, DateOnly Date, ulong UserId, EventNotificationKind Kind)> messages, DateTimeOffset now)
    {
        await using var context = await factory.CreateDbContextAsync();
        long stamp = now.ToUnixTimeSeconds();
        foreach (var message in messages.Distinct())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO EventNotifications (EventId, Date, UserId, Kind, CreatedUtc, NextAttemptUtc, Attempts, Sent, Failed)
                VALUES ({message.EventId}, {Date(message.Date)}, {Id(message.UserId)}, {(int)message.Kind}, {stamp}, {stamp}, 0, 0, 0)
                """);
        }
    }

    public async Task<int> EnqueueDueInvitesAsync(DateTimeOffset now, DateTimeOffset windowEnd)
    {
        await using var context = await factory.CreateDbContextAsync();
        long stamp = now.ToUnixTimeSeconds(), until = windowEnd.ToUnixTimeSeconds();
        // One invitation per invitee and date, only for dates about to happen, so a long series doesn't send every
        // invitation at once. People who already answered (on the dashboard) don't need one.
        return await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT OR IGNORE INTO EventNotifications (EventId, Date, UserId, Kind, CreatedUtc, NextAttemptUtc, Attempts, Sent, Failed)
            SELECT o.EventId, o.Date, i.UserId, 0, {stamp}, {stamp}, 0, 0, 0
            FROM EventOccurrences o
            JOIN Events e ON e.Id = o.EventId
            JOIN EventInvitees i ON i.EventId = o.EventId
            WHERE o.Status <> 2 AND e.CanceledUtc IS NULL AND o.StartUtc > {stamp} AND o.StartUtc <= {until}
              AND NOT EXISTS (SELECT 1 FROM EventResponses r WHERE r.EventId = o.EventId AND r.Date = o.Date AND r.UserId = i.UserId)
            """);
    }

    public async Task<IReadOnlyList<EventOutboxItem>> DueMessagesAsync(DateTimeOffset now, int limit)
    {
        await using var context = await factory.CreateDbContextAsync();
        long stamp = now.ToUnixTimeSeconds();
        var due = await context.Set<EventNotificationRecord>()
            .Where(item => !item.Sent && !item.Failed && item.NextAttemptUtc <= stamp)
            .OrderBy(item => item.NextAttemptUtc).Take(limit).ToListAsync();
        var eventIds = due.Select(item => item.EventId).Distinct().ToArray();
        var events = await context.Set<EventRecord>().Where(item => eventIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        var occurrences = await context.Set<EventOccurrenceRecord>().Where(item => eventIds.Contains(item.EventId)).ToListAsync();
        return due.Where(item => events.ContainsKey(item.EventId)).Select(item =>
        {
            var occurrence = occurrences.First(row => row.EventId == item.EventId && row.Date == item.Date);
            return new EventOutboxItem(ToEvent(events[item.EventId]), ToOccurrence(occurrence), ulong.Parse(item.UserId, CultureInfo.InvariantCulture),
                (EventNotificationKind)item.Kind, item.Attempts);
        }).ToArray();
    }

    public async Task MarkAsync(Guid eventId, DateOnly day, ulong userId, EventNotificationKind kind, bool sent, string? failure, DateTimeOffset? retryAt)
    {
        await using var context = await factory.CreateDbContextAsync();
        var row = await context.Set<EventNotificationRecord>().FindAsync(eventId, Date(day), Id(userId), (int)kind);
        if (row is null)
            return;
        row.Attempts++;
        row.Sent = sent;
        row.Failed = !sent && retryAt is null;
        row.Error = sent ? null : failure is { Length: > 300 } ? failure[..300] : failure;
        if (retryAt is { } next)
            row.NextAttemptUtc = next.ToUnixTimeSeconds();
        await context.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<EventDetails>> LoadAsync(HappyMixDbContext context, List<EventRecord> rows, long? since)
    {
        var ids = rows.Select(row => row.Id).ToArray();
        var invitees = await context.Set<EventInviteeRecord>().Where(item => ids.Contains(item.EventId)).ToListAsync();
        var occurrences = await context.Set<EventOccurrenceRecord>().Where(item => ids.Contains(item.EventId) && (since == null || item.EndUtc >= since))
            .OrderBy(item => item.StartUtc).ToListAsync();
        var responses = await context.Set<EventResponseRecord>().Where(item => ids.Contains(item.EventId)).ToListAsync();
        var deliveries = await context.Set<EventNotificationRecord>().Where(item => ids.Contains(item.EventId)).ToListAsync();
        return rows.Select(row => new EventDetails(ToEvent(row),
            invitees.Where(item => item.EventId == row.Id).Select(item => ulong.Parse(item.UserId, CultureInfo.InvariantCulture)).ToArray(),
            occurrences.Where(item => item.EventId == row.Id).Select(ToOccurrence).ToArray(),
            responses.Where(item => item.EventId == row.Id).Select(item => new EventResponse(item.EventId, ParseDate(item.Date),
                ulong.Parse(item.UserId, CultureInfo.InvariantCulture), item.Accepted, item.Withdrawn, DateTimeOffset.FromUnixTimeSeconds(item.RespondedUtc))).ToArray(),
            deliveries.Where(item => item.EventId == row.Id).Select(item => new EventDelivery(item.EventId, ParseDate(item.Date),
                ulong.Parse(item.UserId, CultureInfo.InvariantCulture), (EventNotificationKind)item.Kind, item.Sent, item.Failed, item.Error)).ToArray()))
            .OrderBy(details => details.Occurrences.Count > 0 ? details.Occurrences[0].Start : DateTimeOffset.MaxValue).ToArray();
    }

    private static EventRecord ToRecord(GuildEvent item) => new()
    {
        Id = item.Id, GuildId = Id(item.GuildId), OrganizerId = Id(item.OrganizerId), GroupId = item.GroupId, Title = item.Title,
        Description = item.Description, TimeZone = item.TimeZone, StartDate = Date(item.StartDate), StartMinute = item.StartTime.Hour * 60 + item.StartTime.Minute,
        DurationMinutes = item.DurationMinutes, Recurrence = (int)item.Recurrence,
        Weekdays = item.Weekdays.Aggregate(0, (mask, day) => mask | (1 << (int)day)),
        UntilDate = item.Until is { } until ? Date(until) : null, CreatedUtc = item.CreatedAt.ToUnixTimeSeconds(),
        CanceledUtc = item.CanceledAt?.ToUnixTimeSeconds()
    };

    private static GuildEvent ToEvent(EventRecord row) => new(row.Id, ulong.Parse(row.GuildId, CultureInfo.InvariantCulture),
        ulong.Parse(row.OrganizerId, CultureInfo.InvariantCulture), row.GroupId, row.Title, row.Description, row.TimeZone,
        ParseDate(row.StartDate), new TimeOnly(row.StartMinute / 60, row.StartMinute % 60), row.DurationMinutes, (EventRecurrence)row.Recurrence,
        Enumerable.Range(0, 7).Where(day => (row.Weekdays & (1 << day)) != 0).Select(day => (DayOfWeek)day).ToArray(),
        row.UntilDate is null ? null : ParseDate(row.UntilDate), DateTimeOffset.FromUnixTimeSeconds(row.CreatedUtc),
        row.CanceledUtc is { } canceled ? DateTimeOffset.FromUnixTimeSeconds(canceled) : null);

    private static EventOccurrence ToOccurrence(EventOccurrenceRecord row) => new(row.EventId, ParseDate(row.Date),
        DateTimeOffset.FromUnixTimeSeconds(row.StartUtc), DateTimeOffset.FromUnixTimeSeconds(row.EndUtc), (OccurrenceStatus)row.Status,
        row.ConfirmedUtc is { } confirmed ? DateTimeOffset.FromUnixTimeSeconds(confirmed) : null,
        row.CanceledUtc is { } canceled ? DateTimeOffset.FromUnixTimeSeconds(canceled) : null);

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Date(DateOnly value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);
}

internal sealed class EventRecord
{
    public Guid Id { get; set; }
    public required string GuildId { get; set; }
    public required string OrganizerId { get; set; }
    public Guid? GroupId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public required string TimeZone { get; set; }
    public required string StartDate { get; set; }
    public int StartMinute { get; set; }
    public int DurationMinutes { get; set; }
    public int Recurrence { get; set; }
    public int Weekdays { get; set; }
    public string? UntilDate { get; set; }
    public long CreatedUtc { get; set; }
    public long? CanceledUtc { get; set; }
}

internal sealed class EventInviteeRecord
{
    public Guid EventId { get; set; }
    public required string UserId { get; set; }
}

internal sealed class EventOccurrenceRecord
{
    public Guid EventId { get; set; }
    public required string Date { get; set; }
    public long StartUtc { get; set; }
    public long EndUtc { get; set; }
    public int Status { get; set; }
    public long? ConfirmedUtc { get; set; }
    public long? CanceledUtc { get; set; }
}

internal sealed class EventResponseRecord
{
    public Guid EventId { get; set; }
    public required string Date { get; set; }
    public required string UserId { get; set; }
    public bool Accepted { get; set; }
    public bool Withdrawn { get; set; }
    public long RespondedUtc { get; set; }
}

internal sealed class EventNotificationRecord
{
    public Guid EventId { get; set; }
    public required string Date { get; set; }
    public required string UserId { get; set; }
    public int Kind { get; set; }
    public long CreatedUtc { get; set; }
    public long NextAttemptUtc { get; set; }
    public int Attempts { get; set; }
    public bool Sent { get; set; }
    public bool Failed { get; set; }
    public string? Error { get; set; }
}
