using Discord.Interactions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Infrastructure.PandaScore;
using MediatR;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Interactions;

public sealed partial class AvailabilityModule
{
    [Group("set", "Set availability using local times.")]
    public sealed class SetModule(ISender sender, IOptions<PandaScoreOptions> options)
        : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("today", "Set today's availability. An earlier end time means tomorrow.")]
        public async Task TodayAsync([Summary(description: "Local start time, e.g. 18:30. Optional for all-day availability.")] string start = "", [Summary(description: "Local end time, e.g. 22:00. Earlier times end the next day.")] string end = "",
            [Autocomplete<AvailabilityTimeZoneAutocomplete>, Summary(description: "Choose a time zone, e.g. Europe/Kyiv. Defaults to the server time zone.")] string? timezone = null, [Summary(description: "True if you might be free, false if definite.")] bool tentative = false, [Summary("all_day")] bool allDay = false)
        {
            await DeferAsync(ephemeral: true);
            var result = await sender.Send(new SetTodayAvailabilityCommand(Context.Guild.Id, Context.User.Id,
                start, end, timezone, options.Value.TimeZoneId, allDay, tentative));
            await ModifyOriginalResponseAsync(message => message.Content = result.IsSuccess
                ? $"Saved {Format(result.Value)}"
                : result.Error.Description);
        }

        [SlashCommand("timetable", "Add a weekly range; matching start and zone updates that range.")]
        public async Task TimetableAsync(DayOfWeek day, [Summary(description: "Local start time, e.g. 18:30. Optional for all-day availability.")] string start = "", [Summary(description: "Local end time, e.g. 22:00. Earlier times end the next day.")] string end = "",
            [Autocomplete<AvailabilityTimeZoneAutocomplete>, Summary(description: "Choose a time zone, e.g. Europe/Kyiv. Defaults to the server time zone.")] string? timezone = null, [Summary(description: "True if you might be free, false if definite.")] bool tentative = false, [Summary("all_day")] bool allDay = false)
        {
            await DeferAsync(ephemeral: true);
            var result = await sender.Send(new SetWeeklyAvailabilityCommand(Context.Guild.Id, Context.User.Id,
                day, start, end, timezone, options.Value.TimeZoneId, allDay, tentative));
            await ModifyOriginalResponseAsync(message => message.Content = result.IsSuccess
                ? $"Saved weekly availability: {FormatWeekly(result.Value)}"
                : result.Error.Description);
        }
    }

    [SlashCommand("timetable", "Show your recurring weekly availability.")]
    public async Task TimetableAsync([MinValue(1)] int page = 1)
    {
        var result = await sender.Send(new GetWeeklyAvailabilityQuery(Context.Guild.Id, Context.User.Id));
        var windows = result.Value;
        int pages = Math.Max(1, (windows.Count + 7) / 8);
        if (page > pages)
        {
            await RespondAsync($"Choose a page from 1 to {pages}.", ephemeral: true);
            return;
        }
        await RespondAsync(windows.Count == 0 ? "No weekly windows. Add one with /availability set timetable."
            : $"**Weekly availability ({page}/{pages})**\n" + string.Join("\n", windows.Skip((page - 1) * 8).Take(8).Select(FormatWeekly)), ephemeral: true);
    }

    [SlashCommand("clear-day", "Remove a day from your weekly timetable.")]
    public async Task ClearDayAsync(DayOfWeek day)
    {
        var result = await sender.Send(new ClearWeeklyAvailabilityCommand(Context.Guild.Id, Context.User.Id, day));
        await RespondAsync(result.IsSuccess ? $"Cleared {day} from your weekly timetable." : result.Error.Description,
            ephemeral: true);
    }

    [SlashCommand("remove-weekly", "Remove one weekly range by day, start time and saved time zone.")]
    public async Task RemoveWeeklyAsync(DayOfWeek day, string start,
        [Autocomplete<AvailabilityTimeZoneAutocomplete>, Summary(description: "Exact saved time zone from /availability timetable, e.g. Europe/Kyiv or UTC-05:00.")] string timezone)
    {
        if (!TimeOnly.TryParseExact(start.Trim(), ["%H", "H:mm"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time))
        {
            await RespondAsync("Enter a 24-hour time like 17 or 00:30.", ephemeral: true);
            return;
        }
        var result = await sender.Send(new ClearWeeklyAvailabilityCommand(Context.Guild.Id, Context.User.Id, day, time, timezone.Trim()));
        await RespondAsync(result.IsSuccess ? "Window removed." : result.Error.Description, ephemeral: true);
    }

    private static string FormatWeekly(WeeklyAvailability window) =>
        (window.IsFullDay ? $"{window.Day}: all day" : $"{window.Day}: {window.Start:HH\\:mm} to {window.End:HH\\:mm}") +
        (window.End < window.Start ? " (next day)" : "") + $" · {window.TimeZone} · {(window.Tentative ? "Tentative" : "Definite")}";
}
