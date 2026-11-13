using System.Globalization;
using System.Text.RegularExpressions;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Bot.Interactions;

/// <summary>Manages the calling member's one-off availability in this server.</summary>
[Group("availability", "Manage your one-off gaming availability.")]
[RequireContext(ContextType.Guild)]
public sealed class AvailabilityModule(IAvailabilityStore store) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("add", "Add a window using date, time and UTC offset.")]
    public Task AddAsync(string start, string end) => SaveAsync(null, start, end);

    [SlashCommand("edit", "Edit a window using its ID from /availability list.")]
    public Task EditAsync(string id, string start, string end) =>
        Guid.TryParse(id, out var key) ? SaveAsync(key, start, end) : RespondAsync("Invalid window ID.", ephemeral: true);

    [SlashCommand("remove", "Remove a window using its ID from /availability list.")]
    public async Task RemoveAsync(string id)
    {
        bool removed = Guid.TryParse(id, out var key) && await store.RemoveAsync(Context.Guild.Id, Context.User.Id, key);
        await RespondAsync(removed ? "Window removed." : "Availability window not found.", ephemeral: true);
    }

    [SlashCommand("list", "Show your upcoming availability and window IDs.")]
    public async Task ListAsync([MinValue(1)] int page = 1)
    {
        const int pageSize = 15;
        var windows = await store.ListAsync(Context.Guild.Id, Context.User.Id);
        var upcoming = windows.Where(window => window.End > DateTimeOffset.UtcNow).ToArray();
        int pages = (upcoming.Length + pageSize - 1) / pageSize;
        if (page > pages)
        {
            await RespondAsync(pages == 0 ? "No upcoming availability." : $"Choose a page from 1 to {pages}.", ephemeral: true);
            return;
        }

        var lines = upcoming.Skip((page - 1) * pageSize).Take(pageSize).Select(Format);
        await RespondAsync($"Upcoming availability ({page}/{pages})\n{string.Join("\n", lines)}", ephemeral: true);
    }

    private async Task SaveAsync(Guid? id, string start, string end)
    {
        if (!TryParse(start, out var from) || !TryParse(end, out var until))
        {
            await RespondAsync("Use a full date, time and UTC offset for both values, e.g. `2026-11-13 20:00 +02:00`.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        try
        {
            var window = await store.SaveAsync(Context.Guild.Id, Context.User.Id, id, from, until);
            await FollowupAsync($"Saved {Format(window)}", ephemeral: true);
        }
        catch (AvailabilityException exception)
        {
            await FollowupAsync(exception.Message, ephemeral: true);
        }
    }

    internal static bool TryParse(string input, out DateTimeOffset value)
    {
        value = default;
        return Regex.IsMatch(input, @"[+-][0-9]{2}:[0-9]{2}$") &&
            DateTimeOffset.TryParseExact(input, "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value);
    }

    private static string Format(AvailabilityWindow window) =>
        $"<t:{window.Start.ToUnixTimeSeconds()}:f> to <t:{window.End.ToUnixTimeSeconds()}:f> · `{window.Id}`";
}
