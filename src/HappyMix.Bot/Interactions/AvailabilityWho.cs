using System.Globalization;
using Discord;
using Discord.Interactions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Bot.Interactions;

/// <summary>/availability who: the server's best time on a date and who has time, from the same query as the dashboard's Members tab.</summary>
public sealed partial class AvailabilityModule
{
    [SlashCommand("who", "See when server members are free today or on a date.")]
    public async Task WhoAsync([Summary(description: "YYYY-MM-DD; today when empty.")] string? date = null)
    {
        DateOnly? requested = null;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                await RespondAsync("Use a date in YYYY-MM-DD format.", ephemeral: true);
                return;
            }
            requested = parsed;
        }
        await DeferAsync(ephemeral: true);
        // Resolved here so the other availability commands don't depend on the member list.
        var memberDirectory = services.GetRequiredService<HappyMix.Bot.Services.IMemberDirectory>();
        string defaultZone = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HappyMix.Infrastructure.PandaScore.PandaScoreOptions>>().Value.TimeZoneId;
        var (profiles, _) = await memberDirectory.GetMembersAsync(Context.Guild.Id);
        var ids = profiles.Values.Where(profile => !profile.IsBot && profile.InServer).Select(profile => profile.Id).ToArray();
        var result = await sender.Send(new GetMembersAvailabilityQuery(Context.Guild.Id, ids, requested, defaultZone));
        if (result.IsFailure)
        {
            await FollowupAsync(result.Error.Description, ephemeral: true);
            return;
        }
        var board = result.Value;
        string Name(ulong id) => Discord.Format.Sanitize(profiles.GetValueOrDefault(id)?.DisplayName ?? id.ToString(CultureInfo.InvariantCulture));
        var free = board.Rows.Where(row => row.HasData).ToArray();
        string day = board.Date == board.Today ? "today" : board.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        if (free.Length == 0)
        {
            await FollowupAsync($"Nobody has set time for {day} yet. Use /availability set today or the dashboard.", ephemeral: true);
            return;
        }

        int best = board.FreeByHour.Max();
        int first = Array.IndexOf(board.FreeByHour, best), last = first;
        while (last + 1 < board.FreeByHour.Length && board.FreeByHour[last + 1] == best)
            last++;
        string bestText = best > 0 ? $"Best time {day}: **{first:00}:00–{(last + 1) % 24:00}:00** · {best} free" : $"No hour {day} where people overlap";
        var lines = free.OrderBy(row => Name(row.UserId), StringComparer.CurrentCultureIgnoreCase).Take(20).Select(row =>
            $"**{Name(row.UserId)}** " + string.Join(", ", row.Ranges.Take(4).Select(range =>
                (range.AllDay ? "all day" : $"{range.Start:HH:mm}–{range.End:HH:mm}") + (range.Tentative ? " (maybe)" : ""))));
        string more = free.Length > 20 ? $"\n…and {free.Length - 20} more on the dashboard." : "";
        await FollowupAsync($"{bestText} ({board.TimeZone})\n{string.Join("\n", lines)}{more}", ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}
