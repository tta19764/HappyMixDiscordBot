using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Watch;
using HappyMix.Application.Guilds;
using HappyMix.Application.Watch;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Bot.Interactions;

/// <summary>
/// /watch: a group's watchlist, from the same commands as the dashboard's Watchlist tab. Adding titles needs the
/// catalog search, so it stays on the dashboard; marking episodes and finishing work here.
/// </summary>
[Group("watch", "Follow a group's watchlist.")]
[RequireContext(ContextType.Guild)]
public sealed class WatchModule(ISender sender) : InteractionModuleBase<SocketInteractionContext>
{
    private GroupActor Actor => new(Context.Guild.Id, Context.User.Id,
        Context.User is SocketGuildUser user && (user.GuildPermissions.ManageGuild || user.GuildPermissions.Administrator));

    [SlashCommand("show", "Show a group's watchlist and where you stopped.")]
    public async Task ShowAsync([Autocomplete<GroupNameAutocomplete>] string group)
    {
        await DeferAsync(ephemeral: true);
        if (await BoardAsync(group) is not { } board)
            return;
        if (board.Entries.Count == 0)
        {
            await Reply("The watchlist is empty. Add titles on the group's Watchlist tab in the dashboard.");
            return;
        }
        var lines = board.Entries.Take(20).Select(entry =>
        {
            var item = entry.Item;
            string status = item.Status switch { WatchStatus.Watching => "Watching", WatchStatus.Watched => "Watched", WatchStatus.Dropped => "Dropped", _ => "Planned" };
            string progress = WatchKinds.HasEpisodes(item.Kind) && item.Status != WatchStatus.Watched
                ? $" · S{item.Season} E{item.Episode}" + (entry.SeasonEpisodes is { } count ? $" of {count}" : "") : "";
            return $"**{Format.Sanitize(item.Title)}** · {status}{progress}";
        });
        await Reply(string.Join("\n", lines) + (board.Entries.Count > 20 ? $"\n…and {board.Entries.Count - 20} more on the dashboard." : ""));
    }

    [SlashCommand("next", "Mark the current episode watched and move to the next one.")]
    public Task NextAsync([Autocomplete<GroupNameAutocomplete>] string group, [Autocomplete<WatchTitleAutocomplete>] string title) =>
        UpdateAsync(group, title, finish: false);

    [SlashCommand("finish", "Mark a whole series or film watched.")]
    public Task FinishAsync([Autocomplete<GroupNameAutocomplete>] string group, [Autocomplete<WatchTitleAutocomplete>] string title) =>
        UpdateAsync(group, title, finish: true);

    private async Task UpdateAsync(string group, string title, bool finish)
    {
        await DeferAsync(ephemeral: true);
        if (await BoardAsync(group) is not { } board)
            return;
        var entry = board.Entries.FirstOrDefault(item => item.Item.Id.ToString("N") == title)
            ?? board.Entries.FirstOrDefault(item => string.Equals(item.Item.Title, title, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            await Reply("That title isn't on the group's watchlist.");
            return;
        }
        var result = await sender.Send(new UpdateWatchItemCommand(Actor, board.GroupId, entry.Item.Id, NextEpisode: !finish, Finish: finish));
        if (result.IsFailure)
        {
            await Reply(result.Error.Description);
            return;
        }
        var item = result.Value;
        await Reply(item.Status == WatchStatus.Watched ? $"**{Format.Sanitize(item.Title)}** is watched."
            : $"**{Format.Sanitize(item.Title)}**: next up S{item.Season} E{item.Episode}.");
    }

    private async Task<WatchBoard?> BoardAsync(string group)
    {
        var found = await sender.Send(new FindGroupQuery(Actor, group));
        var board = found.IsSuccess ? await sender.Send(new GetWatchlistQuery(Actor, found.Value)) : null;
        if (board is { IsSuccess: true })
            return board.Value;
        await Reply(found.IsFailure ? found.Error.Description : board!.Error.Description);
        return null;
    }

    private Task<IUserMessage> Reply(string text) => FollowupAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
}

/// <summary>Titles on the watchlist of the group picked in the same command.</summary>
public sealed class WatchTitleAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.Guild is null || autocompleteInteraction.Data.Options.FirstOrDefault(option => option.Name == "group")?.Value is not string group)
            return AutocompletionResult.FromSuccess();
        var sender = services.GetRequiredService<ISender>();
        bool moderate = context.User is IGuildUser user && (user.GuildPermissions.ManageGuild || user.GuildPermissions.Administrator);
        var actor = new GroupActor(context.Guild.Id, context.User.Id, moderate);
        var found = await sender.Send(new FindGroupQuery(actor, group));
        if (found.IsFailure || await sender.Send(new GetWatchlistQuery(actor, found.Value)) is not { IsSuccess: true } board)
            return AutocompletionResult.FromSuccess();
        string query = (autocompleteInteraction.Data.Current.Value as string ?? "").Trim();
        return AutocompletionResult.FromSuccess(board.Value.Entries
            .Where(entry => entry.Item.Status != WatchStatus.Watched && entry.Item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(25).Select(entry => new AutocompleteResult(
                entry.Item.Title.Length > 90 ? entry.Item.Title[..90] : entry.Item.Title, entry.Item.Id.ToString("N"))));
    }
}
