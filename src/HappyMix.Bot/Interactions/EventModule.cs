using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Events;
using HappyMix.Infrastructure.PandaScore;
using MediatR;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Interactions;

[Group("event", "Create events, respond to invitations, and manage your plans.")]
[RequireContext(ContextType.Guild)]
public sealed class EventModule(ISender sender, IOptions<PandaScoreOptions> options)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("list", "List your events and invitations, with IDs for other event commands.")]
    public async Task ListAsync([MinValue(1)] int page = 1)
    {
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new GetMemberEventsQuery(Context.Guild.Id, Context.User.Id));
        var lines = result.Value.Events.Skip((page - 1) * 5).Take(5)
            .Select(item => $"**{Format.Sanitize(item.Event.Title)}** · `{item.Event.Id}`");
        await ReplyAsync(string.Join("\n", lines.DefaultIfEmpty("No events on this page.")));
    }

    [SlashCommand("groups", "List groups you can create events for, with their IDs.")]
    public async Task GroupsAsync([MinValue(1)] int page = 1)
    {
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new GetEventGroupsQuery(Context.Guild.Id, Context.User.Id));
        var lines = result.Value.Skip((page - 1) * 8).Take(8)
            .Select(group => $"**{Format.Sanitize(group.Name)}** · `{group.Id}`");
        await ReplyAsync(string.Join("\n", lines.DefaultIfEmpty("No groups on this page.")));
    }

    [SlashCommand("show", "Show event dates and response counts.")]
    public async Task ShowAsync(string id, [MinValue(1)] int page = 1)
    {
        if (!Guid.TryParse(id, out var eventId))
        {
            await RespondAsync("Copy the event ID from /event list.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new GetEventQuery(Context.Guild.Id, Context.User.Id, eventId));
        if (result.IsFailure)
        {
            await ReplyAsync(result.Error.Description);
            return;
        }

        var details = result.Value.Details;
        var lines = details.Occurrences.OrderBy(item => item.Date).Skip((page - 1) * 10).Take(10).Select(item =>
        {
            var count = EventRules.Count(details, item.Date);
            return $"`{item.Date:yyyy-MM-dd}` · <t:{item.Start.ToUnixTimeSeconds()}:f> · {item.Status} · {count.Accepted} going";
        });
        await ReplyAsync($"**{Format.Sanitize(details.Event.Title)}**\n{string.Join("\n", lines.DefaultIfEmpty("No dates on this page."))}");
    }

    [SlashCommand("create", "Plan an event for a group or selected server members.")]
    public async Task CreateAsync(string title, [Summary(description: "First date, YYYY-MM-DD.")] string date,
        [Summary(description: "Local time, e.g. 19:30.")] string start,
        [Summary(description: "Local end time, e.g. 22:30.")] string end,
        [Summary(description: "Group ID from /event groups.")] string? group = null,
        [Summary(description: "Discord user mentions or IDs separated by spaces or commas.")] string? people = null,
        [Autocomplete<AvailabilityTimeZoneAutocomplete>] string? timezone = null,
        EventRecurrence repeat = EventRecurrence.Once,
        [Summary(description: "Comma-separated weekday names, e.g. Monday,Wednesday,Friday.")] string? weekdays = null,
        [Summary(description: "Last date for a repeating event, YYYY-MM-DD.")] string? until = null,
        string? description = null)
    {
        if (!ParseDate(date, out var first) || (until is not null && !ParseDate(until, out _)) ||
            (group is not null && !Guid.TryParse(group, out _)) || (group is null) == (people is null))
        {
            await RespondAsync("Use YYYY-MM-DD and supply either a group ID or invited people.", ephemeral: true);
            return;
        }
        var days = new List<DayOfWeek>();
        foreach (string name in Split(weekdays))
        {
            if (!Enum.TryParse<DayOfWeek>(name, true, out var day) || !Enum.IsDefined(day))
            {
                await RespondAsync("Enter weekday names separated by commas.", ephemeral: true);
                return;
            }
            days.Add(day);
        }

        await DeferAsync(ephemeral: true);
        var invitees = new HashSet<ulong>();
        foreach (string mention in Split(people))
        {
            if (!ulong.TryParse(mention.Trim('<', '@', '!', '>'), CultureInfo.InvariantCulture, out ulong userId) ||
                await ((IGuild)Context.Guild).GetUserAsync(userId, CacheMode.AllowDownload) is not { IsBot: false })
            {
                await ReplyAsync("Use Discord mentions or IDs for current human members of this server.");
                return;
            }
            invitees.Add(userId);
        }

        var requestId = new Guid((int)Context.Interaction.Id, (short)(Context.Interaction.Id >> 32),
            (short)(Context.Interaction.Id >> 48), 0, 0, 0, 0, 0, 0, 0, 1);
        var result = await sender.Send(new CreateEventCommand(Context.Guild.Id, Context.User.Id, requestId,
            group is null ? null : Guid.Parse(group), invitees.ToArray(), title, description, first, start, end, timezone,
            options.Value.TimeZoneId, repeat, days, ParseDate(until, out var last) ? last : null));
        await ReplyAsync(result.IsSuccess ? $"Event saved. ID: `{result.Value.EventId}`" : result.Error.Description);
    }

    [SlashCommand("respond", "Accept, decline, or withdraw from an event date.")]
    public async Task RespondToEventAsync(string id, string date, bool attending)
    {
        if (!Guid.TryParse(id, out var eventId) || !ParseDate(date, out var day))
        {
            await RespondAsync("Enter an event ID and a date in YYYY-MM-DD format.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new RespondToEventCommand(Context.Guild.Id, Context.User.Id, eventId, day, attending));
        await ReplyAsync(result.IsFailure ? result.Error.Description : attending ? "Accepted." : "You are no longer attending.");
    }

    [SlashCommand("confirm", "Confirm an event date you organize.")]
    public async Task ConfirmAsync(string id, string date)
    {
        if (!Guid.TryParse(id, out var eventId) || !ParseDate(date, out var day))
        {
            await RespondAsync("Enter an event ID and a date in YYYY-MM-DD format.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new ConfirmEventCommand(Context.Guild.Id, Context.User.Id, eventId, day));
        await ReplyAsync(result.IsSuccess ? $"Confirmed with {result.Value.Accepted} attending." : result.Error.Description);
    }

    [SlashCommand("cancel", "Cancel a date or explicitly cancel the entire series.")]
    public async Task CancelAsync(string id, [Summary("whole-series")] bool wholeSeries, string? date = null)
    {
        if (!Guid.TryParse(id, out var eventId) || (wholeSeries ? date is not null : !ParseDate(date, out _)))
        {
            await RespondAsync("For one date, enter YYYY-MM-DD. To cancel the whole series, select whole-series and omit the date.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new CancelEventCommand(Context.Guild.Id, Context.User.Id, eventId,
            ParseDate(date, out var day) ? day : null));
        await ReplyAsync(result.IsSuccess ? $"Canceled {result.Value} date(s)." : result.Error.Description);
    }

    private Task<IUserMessage> ReplyAsync(string message) => FollowupAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
    private static bool ParseDate(string? date, out DateOnly value) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    private static string[] Split(string? text) => (text ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
}
