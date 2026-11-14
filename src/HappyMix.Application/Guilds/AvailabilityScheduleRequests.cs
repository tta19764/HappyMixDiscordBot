using System.Globalization;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public sealed record SetTodayAvailabilityCommand(ulong GuildId, ulong UserId, string Start, string End,
    string? Offset, string DefaultTimeZone) : ICommand<AvailabilityWindow>;
public sealed record SetWeeklyAvailabilityCommand(ulong GuildId, ulong UserId, DayOfWeek Day, string Start, string End,
    string? Offset, string DefaultTimeZone) : ICommand<WeeklyAvailability>;
public sealed record GetWeeklyAvailabilityQuery(ulong GuildId, ulong UserId) : IQuery<IReadOnlyList<WeeklyAvailability>>;
public sealed record ClearWeeklyAvailabilityCommand(ulong GuildId, ulong UserId, DayOfWeek Day) : ICommand;

internal sealed class AvailabilityScheduleHandlers(IAvailabilityStore store, IGuildSettingsStore settings, IClock clock) :
    ICommandHandler<SetTodayAvailabilityCommand, AvailabilityWindow>,
    ICommandHandler<SetWeeklyAvailabilityCommand, WeeklyAvailability>,
    IQueryHandler<GetWeeklyAvailabilityQuery, IReadOnlyList<WeeklyAvailability>>,
    ICommandHandler<ClearWeeklyAvailabilityCommand>
{
    public async Task<Result<AvailabilityWindow>> Handle(SetTodayAvailabilityCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var (start, end) = ParseTimes(command.Start, command.End);
            var zone = await GetZoneAsync(command.GuildId, command.Offset, command.DefaultTimeZone, cancellationToken);
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
            var from = Resolve(date.ToDateTime(start), zone);
            var until = Resolve((end < start ? date.AddDays(1) : date).ToDateTime(end), zone);
            if (until <= clock.UtcNow)
                throw new AvailabilityException("This window has already ended.");

            return Result.Success(await store.SaveAsync(command.GuildId, command.UserId, null, from, until));
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

            var (start, end) = ParseTimes(command.Start, command.End);
            var zone = await GetZoneAsync(command.GuildId, command.Offset, command.DefaultTimeZone, cancellationToken);
            var window = new WeeklyAvailability(command.Day, start, end, zone.Id);
            await store.SaveWeeklyAsync(command.GuildId, command.UserId, window);
            return Result.Success(window);
        }
        catch (AvailabilityException exception)
        {
            return Result.Failure<WeeklyAvailability>(new("Availability.Invalid", exception.Message));
        }
    }

    public async Task<Result<IReadOnlyList<WeeklyAvailability>>> Handle(GetWeeklyAvailabilityQuery query,
        CancellationToken cancellationToken) => Result.Success(await store.ListWeeklyAsync(query.GuildId, query.UserId));

    public async Task<Result> Handle(ClearWeeklyAvailabilityCommand command, CancellationToken cancellationToken) =>
        await store.RemoveWeeklyAsync(command.GuildId, command.UserId, command.Day)
            ? Result.Success()
            : Result.Failure(new("Availability.NotFound", "No weekly window is set for that day."));

    private async Task<TimeZoneInfo> GetZoneAsync(ulong guildId, string? offset, string defaultZone,
        CancellationToken cancellationToken)
    {
        if (offset is not null)
        {
            offset = offset.Trim();
            if (!DateTimeOffset.TryParseExact("2000-01-01 " + offset, "yyyy-MM-dd zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var fixedTime))
                throw new AvailabilityException("Use an offset like +02:00, -05:00, or +00:00.");

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
}
