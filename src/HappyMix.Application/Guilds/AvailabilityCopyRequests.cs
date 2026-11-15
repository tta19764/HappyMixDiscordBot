using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>
/// Copies every weekly range of <paramref name="From"/> onto each day in <paramref name="To"/>, replacing what those days
/// had. Returns how many days changed. A target that can't take the ranges (they would overlap a neighbouring day) is
/// restored and the copy stops with an error.
/// </summary>
public sealed record CopyWeeklyDayCommand(ulong GuildId, ulong UserId, DayOfWeek From, IReadOnlyList<DayOfWeek> To) : ICommand<int>;

/// <summary>
/// Copies the one-off windows that start on <paramref name="From"/> to <paramref name="To"/>, at the same local times in
/// the server time zone, replacing the windows that started on <paramref name="To"/>. Returns how many windows were copied.
/// </summary>
public sealed record CopyDateAvailabilityCommand(ulong GuildId, ulong UserId, DateOnly From, DateOnly To, string DefaultZone) : ICommand<int>;

internal sealed partial class AvailabilityScheduleHandlers :
    ICommandHandler<CopyWeeklyDayCommand, int>,
    ICommandHandler<CopyDateAvailabilityCommand, int>
{
    public async Task<Result<int>> Handle(CopyWeeklyDayCommand command, CancellationToken cancellationToken)
    {
        var targets = command.To.Distinct().Where(day => day != command.From).ToArray();
        if (!Enum.IsDefined(command.From) || targets.Length == 0 || !targets.All(Enum.IsDefined))
            return Result.Failure<int>(new("Availability.Invalid", "Choose at least one other day to copy to."));

        ulong member = await MemberAsync(command.GuildId, command.UserId);
        var all = await store.ListWeeklyAsync(command.GuildId, member);
        var source = all.Where(window => window.Day == command.From).ToArray();
        foreach (var day in targets)
        {
            var previous = all.Where(window => window.Day == day).ToArray();
            try
            {
                await store.RemoveWeeklyAsync(command.GuildId, member, day);
                foreach (var zone in source.GroupBy(window => window.TimeZone))
                    await store.ReplaceWeeklyDayAsync(command.GuildId, member, day, zone.Key,
                        zone.Select(window => window with { Day = day }).ToArray());
            }
            catch (AvailabilityException exception)
            {
                await store.RemoveWeeklyAsync(command.GuildId, member, day);
                foreach (var zone in previous.GroupBy(window => window.TimeZone))
                    await store.ReplaceWeeklyDayAsync(command.GuildId, member, day, zone.Key, zone.ToArray());
                return Result.Failure<int>(new("Availability.Invalid", exception.Message));
            }
        }
        return Result.Success(targets.Length);
    }

    public async Task<Result<int>> Handle(CopyDateAvailabilityCommand command, CancellationToken cancellationToken)
    {
        if (command.From == command.To)
            return Result.Failure<int>(new("Availability.Invalid", "Drop the date on a different day."));

        TimeZoneInfo zone;
        try { zone = await GetZoneAsync(command.GuildId, null, command.DefaultZone, cancellationToken); }
        catch (AvailabilityException exception) { return Result.Failure<int>(new("Availability.Invalid", exception.Message)); }

        ulong member = await MemberAsync(command.GuildId, command.UserId);
        var windows = await store.ListAsync(command.GuildId, member);
        DateOnly StartDay(AvailabilityWindow window) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.Start, zone).DateTime);
        int shift = command.To.DayNumber - command.From.DayNumber;
        DateTimeOffset Move(DateTimeOffset instant) =>
            LocalTimes.ToInstant(TimeZoneInfo.ConvertTime(instant, zone).DateTime.AddDays(shift), zone);

        var copies = windows.Where(window => StartDay(window) == command.From)
            .Select(window => new AvailabilityWindow(Guid.NewGuid(), Move(window.Start), Move(window.End), window.Tentative)).ToArray();
        var replaced = windows.Where(window => StartDay(window) == command.To).ToArray();
        foreach (var window in replaced)
            await store.RemoveAsync(command.GuildId, member, window.Id);
        try
        {
            await store.SaveDatesAsync(command.GuildId, member, copies);
        }
        catch (AvailabilityException exception)
        {
            await store.SaveDatesAsync(command.GuildId, member, replaced);
            return Result.Failure<int>(new("Availability.Invalid", exception.Message));
        }
        return Result.Success(copies.Length);
    }
}
