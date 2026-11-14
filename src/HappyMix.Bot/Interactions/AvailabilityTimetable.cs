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
        public async Task TodayAsync(string start, string end,
            [Summary(description: "Optional UTC offset, e.g. +02:00. Defaults to the server time zone.")] string? offset = null)
        {
            await DeferAsync(ephemeral: true);
            var result = await sender.Send(new SetTodayAvailabilityCommand(Context.Guild.Id, Context.User.Id,
                start, end, offset, options.Value.TimeZoneId));
            await ModifyOriginalResponseAsync(message => message.Content = result.IsSuccess
                ? $"Saved {Format(result.Value)}"
                : result.Error.Description);
        }

        [SlashCommand("timetable", "Set or replace a recurring weekly window for one day.")]
        public async Task TimetableAsync(DayOfWeek day, string start, string end,
            [Summary(description: "Optional UTC offset, e.g. +02:00. Defaults to the server time zone.")] string? offset = null)
        {
            await DeferAsync(ephemeral: true);
            var result = await sender.Send(new SetWeeklyAvailabilityCommand(Context.Guild.Id, Context.User.Id,
                day, start, end, offset, options.Value.TimeZoneId));
            await ModifyOriginalResponseAsync(message => message.Content = result.IsSuccess
                ? $"Saved weekly availability: {FormatWeekly(result.Value)}"
                : result.Error.Description);
        }
    }

    [SlashCommand("timetable", "Show your recurring weekly availability.")]
    public async Task TimetableAsync()
    {
        var result = await sender.Send(new GetWeeklyAvailabilityQuery(Context.Guild.Id, Context.User.Id));
        var windows = result.Value;
        await RespondAsync(windows.Count == 0 ? "No weekly windows. Add one with /availability set timetable."
            : "**Weekly availability**\n" + string.Join("\n", windows.Select(FormatWeekly)), ephemeral: true);
    }

    [SlashCommand("clear-day", "Remove a day from your weekly timetable.")]
    public async Task ClearDayAsync(DayOfWeek day)
    {
        var result = await sender.Send(new ClearWeeklyAvailabilityCommand(Context.Guild.Id, Context.User.Id, day));
        await RespondAsync(result.IsSuccess ? $"Cleared {day} from your weekly timetable." : result.Error.Description,
            ephemeral: true);
    }

    private static string FormatWeekly(WeeklyAvailability window) =>
        $"{window.Day}: {window.Start:HH\\:mm} to {window.End:HH\\:mm}" +
        (window.End < window.Start ? " (next day)" : "") + $" · {window.TimeZone}";
}
