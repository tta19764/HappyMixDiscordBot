using System.Globalization;
using Discord;
using Discord.Interactions;
using HappyMix.Application.Guilds;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.Extensions.DependencyInjection;
using HappyMix.Infrastructure.PandaScore;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Interactions;

public sealed partial class GuildGroupModule
{
    [SlashCommand("games", "List games attached to a group.")]
    public async Task GamesAsync(string name)
    {
        await DeferAsync(ephemeral: true);
        var group = await FindVisibleGroupAsync(name);
        if (group is not { CanViewRoster: true })
        {
            await ReplyAsync("You need access to the group roster.");
            return;
        }
        if (group.Games.Length == 0)
        {
            await ReplyAsync("This group has no attached games.");
            return;
        }
        foreach (var page in group.Games.Chunk(8))
            await FollowupAsync(string.Join("\n", page.Select(game => $"**{Format.Sanitize(game.Name)}** Â· `{game.Id}`")),
                ephemeral: true, allowedMentions: AllowedMentions.None);
        await DeleteOriginalResponseAsync();
    }

    [SlashCommand("game-add", "Attach a game to a group.")]
    public Task AddGameAsync(string name, string game, string? steam = null) =>
        ChangeGameAsync(name, id => new AddGroupGameCommand(Actor, id, game, steam));

    [SlashCommand("game-edit", "Edit an attached game using its ID from /group games.")]
    public Task EditGameAsync(string name, [Summary("game_id")] string gameId, string game, string? steam = null) =>
        Guid.TryParse(gameId, out var id)
            ? ChangeGameAsync(name, group => new UpdateGroupGameCommand(Actor, group, id, game, steam))
            : RespondAsync("Enter a game ID from /group games.", ephemeral: true);

    [SlashCommand("game-remove", "Remove an attached game using its ID from /group games.")]
    public Task RemoveGameAsync(string name, [Summary("game_id")] string gameId) =>
        Guid.TryParse(gameId, out var id)
            ? ChangeGameAsync(name, group => new RemoveGroupGameCommand(Actor, group, id))
            : RespondAsync("Enter a game ID from /group games.", ephemeral: true);

    [SlashCommand("availability", "Show when group members are free on a date.")]
    public async Task AvailabilityAsync(string name, string? date = null, [MinValue(1)] int page = 1)
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
        var group = await FindVisibleGroupAsync(name);
        if (group is not { CanViewRoster: true })
        {
            await ReplyAsync("You need access to the group roster.");
            return;
        }
        var result = await sender.Send(new GetGroupAvailabilityBoardQuery(Actor, group.Summary.Id, requested,
            options.Value.TimeZoneId));
        if (result.IsFailure)
        {
            await ReplyAsync(result.Error.Description);
            return;
        }
        var board = result.Value;
        if (requested is not null && requested != board.Date)
        {
            await ReplyAsync("Choose a date within the board's next seven days.");
            return;
        }
        var pages = board.Rows.Chunk(4).ToArray();
        if (page < 1 || page > pages.Length)
        {
            await ReplyAsync(pages.Length == 0 ? "This group has no members." : $"Choose a page from 1 to {pages.Length}.");
            return;
        }
        var rows = pages[page - 1].Select(row =>
        {
            string displayName = Context.Guild.GetUser(row.UserId)?.DisplayName ?? row.UserId.ToString(CultureInfo.InvariantCulture);
            var ranges = row.Ranges.Take(8).Select(range =>
                (range.AllDay ? "All day" : $"{range.Start:HH:mm}–{range.End:HH:mm}") +
                (range.Source == AvailabilitySource.Weekly ? " (weekly)" : "") +
                (range.Tentative ? " · tentative" : " · definite"));
            string times = row.Ranges.Length == 0 ? "No data" : string.Join(", ", ranges);
            return $"**{Format.Sanitize(displayName)}** {times}" + (row.Ranges.Length > 8 ? " …" : "");
        });
        await ReplyAsync($"**{board.Date:yyyy-MM-dd} ({board.TimeZone})** · Page {page}/{pages.Length}\n{string.Join("\n", rows)}");
    }

    private async Task ChangeGameAsync(string name, Func<Guid, GroupCommand> create)
    {
        await DeferAsync(ephemeral: true);
        var group = await FindVisibleGroupAsync(name);
        if (group is not { Summary.CanManage: true })
        {
            await ReplyAsync("Only the group's organizer or a moderator can manage games.");
            return;
        }
        var result = await sender.Send(create(group.Summary.Id));
        await ReplyAsync(result.IsSuccess ? "Group games updated." : result.Error.Description);
    }

    private async Task<GroupDetail?> FindVisibleGroupAsync(string name)
    {
        var groups = (await sender.Send(new GetVisibleGroupsQuery(Actor, false))).Value;
        var group = groups.FirstOrDefault(group => GuildGroup.GetNameKey(group.Name) == GuildGroup.GetNameKey(name));
        if (group is null)
            return null;
        var detail = await sender.Send(new GetGroupDetailQuery(Actor, group.Id));
        return detail.IsSuccess ? detail.Value : null;
    }
}
