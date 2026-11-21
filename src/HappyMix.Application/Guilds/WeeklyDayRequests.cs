using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>
/// One range from the weekly day editor. An end at or before the start runs into the next day.
/// <paramref name="DoNotDisturb"/> marks do-not-disturb hours, which the editor saves separately (<c>SetQuietDayCommand</c>).
/// </summary>
public sealed record WeeklyRangeInput(string Start, string End, bool FullDay = false, bool Tentative = false, bool DoNotDisturb = false);

/// <summary>
/// Replaces every weekly range on one day in one time zone with <paramref name="Ranges"/>, atomically. This is how
/// the dashboard edits existing ranges: the editor sends the whole day back. Ranges on that day in other time zones,
/// and every other day, stay as they are. An empty list clears the day in that zone.
/// </summary>
public sealed record SetWeeklyDayCommand(ulong GuildId, ulong UserId, DayOfWeek Day, IReadOnlyList<WeeklyRangeInput> Ranges,
    string? Offset, string DefaultTimeZone) : ICommand<IReadOnlyList<WeeklyAvailability>>;

internal sealed partial class AvailabilityScheduleHandlers : ICommandHandler<SetWeeklyDayCommand, IReadOnlyList<WeeklyAvailability>>
{
    internal const int MaxRangesPerDay = 8;

    public async Task<Result<IReadOnlyList<WeeklyAvailability>>> Handle(SetWeeklyDayCommand command, CancellationToken cancellationToken)
    {
        try
        {
            if (!Enum.IsDefined(command.Day))
                throw new AvailabilityException("Choose a valid day of the week.");
            if (command.Ranges.Count > MaxRangesPerDay)
                throw new AvailabilityException("Use up to 8 ranges per day.");

            var zone = await GetZoneAsync(command.GuildId, command.Offset, command.DefaultTimeZone, cancellationToken);
            var ranges = command.Ranges.Select(range =>
            {
                var (start, end) = range.FullDay ? (TimeOnly.MinValue, TimeOnly.MinValue) : ParseTimes(range.Start, range.End);
                return new WeeklyAvailability(command.Day, start, end, zone.Id, range.Tentative);
            }).OrderBy(range => range.Start).ToArray();

            if (ranges.Length > 1 && ranges.Any(range => range.IsFullDay))
                throw new AvailabilityException("A full day can't be combined with other ranges on the same day.");
            for (int index = 1; index < ranges.Length; index++)
            {
                // Sorted by start, so a range overlaps the next one when it ends after that one starts.
                var previous = ranges[index - 1];
                double previousEnd = previous.End <= previous.Start ? 24 + previous.End.ToTimeSpan().TotalHours : previous.End.ToTimeSpan().TotalHours;
                if (previousEnd > ranges[index].Start.ToTimeSpan().TotalHours)
                    throw new AvailabilityException("These ranges overlap. Adjust them so each one starts after the previous one ends.");
            }

            ulong member = await MemberAsync(command.GuildId, command.UserId);
            await store.ReplaceWeeklyDayAsync(command.GuildId, member, command.Day, zone.Id, ranges);
            return Result.Success<IReadOnlyList<WeeklyAvailability>>(ranges);
        }
        catch (AvailabilityException exception)
        {
            return Result.Failure<IReadOnlyList<WeeklyAvailability>>(new("Availability.Invalid", exception.Message));
        }
    }
}
