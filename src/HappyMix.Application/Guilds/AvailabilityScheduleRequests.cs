using System.Globalization;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>Today's window. <paramref name="FullDay"/> saves local midnight today through local midnight tomorrow.</summary>
public sealed record SetTodayAvailabilityCommand(ulong GuildId, ulong UserId, string Start, string End,
    string? Offset, string DefaultTimeZone, bool FullDay = false, bool Tentative = false) : ICommand<AvailabilityWindow>;
/// <summary>A weekly window. <paramref name="FullDay"/> saves 00:00–00:00, the whole local day.</summary>
public sealed record SetWeeklyAvailabilityCommand(ulong GuildId, ulong UserId, DayOfWeek Day, string Start, string End,
    string? Offset, string DefaultTimeZone, bool FullDay = false, bool Tentative = false) : ICommand<WeeklyAvailability>;
public sealed record GetWeeklyAvailabilityQuery(ulong GuildId, ulong UserId) : IQuery<IReadOnlyList<WeeklyAvailability>>;
public sealed record ClearWeeklyAvailabilityCommand(ulong GuildId, ulong UserId, DayOfWeek Day, TimeOnly? Start = null, string? TimeZone = null) : ICommand;

internal sealed class AvailabilityScheduleHandlers(IAvailabilityStore store, IGuildSettingsStore settings, IClock clock,
    IAccountLinkStore accountLinks) :
    ICommandHandler<SetTodayAvailabilityCommand, AvailabilityWindow>,
    ICommandHandler<SetWeeklyAvailabilityCommand, WeeklyAvailability>,
    IQueryHandler<GetWeeklyAvailabilityQuery, IReadOnlyList<WeeklyAvailability>>,
    ICommandHandler<ClearWeeklyAvailabilityCommand>
{
    public async Task<Result<AvailabilityWindow>> Handle(SetTodayAvailabilityCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var zone = await GetZoneAsync(command.GuildId, command.Offset, command.DefaultTimeZone, cancellationToken);
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
            if (command.FullDay)
                return Result.Success(await store.SaveAsync(command.GuildId, await MemberAsync(command.GuildId, command.UserId), null,
                    LocalTimes.StartOfDay(date, zone), LocalTimes.StartOfDay(date.AddDays(1), zone), command.Tentative));

            var (start, end) = ParseTimes(command.Start, command.End);
            var from = Resolve(date.ToDateTime(start), zone);
            var until = Resolve((end < start ? date.AddDays(1) : date).ToDateTime(end), zone);
            if (until <= clock.UtcNow)
                throw new AvailabilityException("This window has already ended.");

            return Result.Success(await store.SaveAsync(command.GuildId, await MemberAsync(command.GuildId, command.UserId), null, from, until, command.Tentative));
        }
        catch (AvailabilityException exception)
        {
            return Result.Failure<AvailabilityWindow>(new("Availability.Invalid", exception.Message));
        }
    }

    public async Task<Result<WeeklyAvailability>> Handle(SetWeeklyAvailabilityCommand command, CancellationToken cancellationToken)
    {
        try
        {
            if (!Enum.IsDefined(command.Day))
                throw new AvailabilityException("Choose a valid day of the week.");

            var (start, end) = command.FullDay ? (TimeOnly.MinValue, TimeOnly.MinValue) : ParseTimes(command.Start, command.End);
            var zone = await GetZoneAsync(command.GuildId, command.Offset, command.DefaultTimeZone, cancellationToken);
            var window = new WeeklyAvailability(command.Day, start, end, zone.Id, command.Tentative);
            await store.SaveWeeklyAsync(command.GuildId, await MemberAsync(command.GuildId, command.UserId), window);
            return Result.Success(window);
        }
        catch (AvailabilityException exception)
        {
            return Result.Failure<WeeklyAvailability>(new("Availability.Invalid", exception.Message));
        }
    }

    public async Task<Result<IReadOnlyList<WeeklyAvailability>>> Handle(GetWeeklyAvailabilityQuery query,
        CancellationToken cancellationToken) => Result.Success(await store.ListWeeklyAsync(query.GuildId, await MemberAsync(query.GuildId, query.UserId)));

    public async Task<Result> Handle(ClearWeeklyAvailabilityCommand command, CancellationToken cancellationToken) =>
        await store.RemoveWeeklyAsync(command.GuildId, await MemberAsync(command.GuildId, command.UserId), command.Day, command.Start, command.TimeZone)
            ? Result.Success()
            : Result.Failure(new("Availability.NotFound", "No weekly window is set for that day."));

    private async Task<TimeZoneInfo> GetZoneAsync(ulong guildId, string? offset, string defaultZone,
        CancellationToken cancellationToken)
    {
        if (offset is not null)
        {
            offset = offset.Trim();
            if (LocalTimes.FindZone(offset) is { } namedZone)
                return namedZone;

            if (!DateTimeOffset.TryParseExact("2000-01-01 " + offset, "yyyy-MM-dd zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var fixedTime))
                throw new AvailabilityException("Choose a time zone like Europe/Kyiv or enter a UTC offset like +02:00.");

            return TimeZoneInfo.CreateCustomTimeZone("UTC" + offset, fixedTime.Offset, "UTC" + offset, "UTC" + offset);
        }

        var saved = await settings.GetAsync(guildId, cancellationToken);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(saved?.TimeZoneId ?? defaultZone, out var zone))
            throw new AvailabilityException("The server time zone is invalid. Supply an offset or ask an administrator to update it.");
        return zone;
    }

    private static (TimeOnly Start, TimeOnly End) ParseTimes(string start, string end)
    {
        string[] formats = ["%H", "H:mm"];
        if (!TimeOnly.TryParseExact(start.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !TimeOnly.TryParseExact(end.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
            throw new AvailabilityException("Enter 24-hour times like 17 or 00:30.");
        if (from == until)
            throw new AvailabilityException("Start and end must be different.");
        return (from, until);
    }

    private static DateTimeOffset Resolve(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new AvailabilityException("This time is skipped or repeated by daylight saving. Supply an explicit UTC offset.");
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>Linked accounts share one schedule, stored on the family's primary account.</summary>
    private async Task<ulong> MemberAsync(ulong guildId, ulong userId) => (await accountLinks.LoadAsync(guildId)).Member(userId);
}

/// <summary>Converts local calendar times to instants without failing on daylight-saving gaps or repeats.</summary>
public static class LocalTimes
{
    public static TimeZoneInfo? FindZone(string id)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone;
        if (id.StartsWith("UTC", StringComparison.Ordinal) && DateTimeOffset.TryParseExact("2000-01-01 " + id[3..],
                "yyyy-MM-dd zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fixedTime))
            return TimeZoneInfo.CreateCustomTimeZone(id, fixedTime.Offset, id, id);
        return null;
    }

    /// <summary>The first instant of a local date. A skipped midnight moves forward to the first valid time.</summary>
    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone) => ToInstant(date.ToDateTime(TimeOnly.MinValue), zone);

    /// <summary>
    /// Resolves a local wall-clock time. Times inside a spring-forward gap move to the end of the gap;
    /// repeated times during fall-back use the earlier instant.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var probe = local;
            while (zone.IsInvalidTime(probe))
                probe = probe.AddMinutes(1);
            return new DateTimeOffset(probe, zone.GetUtcOffset(probe));
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
