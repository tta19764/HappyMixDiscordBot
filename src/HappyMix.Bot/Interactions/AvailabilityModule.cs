using System.Globalization;
using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using MediatR;

namespace HappyMix.Bot.Interactions;

/// <summary>Manages the calling member's one-off availability in this server.</summary>
[Group("availability", "Manage your gaming availability.")]
[RequireContext(ContextType.Guild)]
public sealed partial class AvailabilityModule(ISender sender, IConfiguration configuration) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("dashboard", "Open the web availability editor.")]
    public async Task DashboardAsync()
    {
        string? url = configuration["Dashboard:PublicUrl"];
        if (!configuration.GetValue<bool>("Dashboard:Enabled") ||
            !Uri.TryCreate(url, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps)
        {
            await RespondAsync("The web dashboard is unavailable. You can use /availability set today or /availability set timetable here.",
                ephemeral: true);
            return;
        }

        var link = new Uri(origin, $"/availability/{Context.Guild.Id}");
        await RespondAsync("Manage your availability in HappyMix.",
            components: new ComponentBuilder().WithButton("Edit availability", style: ButtonStyle.Link,
                url: link.AbsoluteUri).Build(), ephemeral: true);
    }

    [SlashCommand("add", "Add availability using local dates and a suggested time zone.")]
    public Task AddAsync( [Summary(description: "Local date and time, e.g. 2026-12-01 18:30.")] string start, [Summary(description: "Local end date and time, e.g. 2026-12-01 22:00.")] string end, [Autocomplete<AvailabilityTimeZoneAutocomplete>, Summary(description: "Choose a time zone, e.g. Europe/Kyiv.")] string timezone, bool tentative = false) => SaveAsync(null, start, end, timezone, tentative);

    [SlashCommand("edit", "Edit a window using its ID from /availability list.")]
    public Task EditAsync(string id,  [Summary(description: "Local date and time, e.g. 2026-12-01 18:30.")] string start, [Summary(description: "Local end date and time, e.g. 2026-12-01 22:00.")] string end, [Autocomplete<AvailabilityTimeZoneAutocomplete>, Summary(description: "Choose a time zone, e.g. Europe/Kyiv.")] string timezone, bool tentative = false) =>
        Guid.TryParse(id, out var key) ? SaveAsync(key, start, end, timezone, tentative) : RespondAsync("Invalid window ID.", ephemeral: true);

    [SlashCommand("remove", "Remove a window using its ID from /availability list.")]
    public async Task RemoveAsync(string id)
    {
        bool removed = Guid.TryParse(id, out var key) && (await sender.Send(new RemoveAvailabilityCommand(Context.Guild.Id, Context.User.Id, key))).IsSuccess;
        await RespondAsync(removed ? "Window removed." : "Availability window not found.", ephemeral: true);
    }

    [SlashCommand("list", "Show your upcoming availability and window IDs.")]
    public async Task ListAsync([MinValue(1)] int page = 1)
    {
        const int pageSize = 8;
        var upcoming = (await sender.Send(new GetUpcomingAvailabilityQuery(Context.Guild.Id, Context.User.Id))).Value;
        int pages = (upcoming.Length + pageSize - 1) / pageSize;
        if (page > pages)
        {
            await RespondAsync(pages == 0 ? "No upcoming availability." : $"Choose a page from 1 to {pages}.", ephemeral: true);
            return;
        }

        var lines = upcoming.Skip((page - 1) * pageSize).Take(pageSize).Select(Format);
        await RespondAsync($"Upcoming availability ({page}/{pages})\n{string.Join("\n", lines)}", ephemeral: true);
    }

    private async Task SaveAsync(Guid? id, string start, string end, string timezone, bool tentative)
    {
        if (!TryParseLocal(start, timezone, out var from) || !TryParseLocal(end, timezone, out var until))
        {
            await RespondAsync("Use local dates and times such as `2026-12-01 20:00` and select a time zone such as `Europe/Kyiv`. Times skipped or repeated by daylight saving are unavailable.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new SaveAvailabilityCommand(Context.Guild.Id, Context.User.Id, id, from, until, tentative));
        await FollowupAsync(result.IsSuccess ? $"Saved {Format(result.Value)}" : result.Error.Description, ephemeral: true);
    }

    private static bool TryParseLocal(string input, string timezone, out DateTimeOffset value)
    {
        value = default;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone) ||
            !DateTime.TryParseExact(input.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local) || zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            return false;
        value = new DateTimeOffset(local, zone.GetUtcOffset(local));
        return true;
    }

    internal static bool TryParse(string input, out DateTimeOffset value)
    {
        value = default;
        return Regex.IsMatch(input, @"[+-][0-9]{2}:[0-9]{2}$") &&
            DateTimeOffset.TryParseExact(input, "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value);
    }

    private static string Format(AvailabilityWindow window) =>
        $"<t:{window.Start.ToUnixTimeSeconds()}:f> to <t:{window.End.ToUnixTimeSeconds()}:f> · {(window.Tentative ? "Tentative" : "Definite")} · `{window.Id}`";
}
