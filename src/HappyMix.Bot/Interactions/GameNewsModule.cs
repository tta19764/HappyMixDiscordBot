using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.News;
using HappyMix.Application.News;
using HappyMix.Bot.Services;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Bot.Interactions;

[Group("game-news", "Follow Steam announcements for games in this server.")]
[RequireContext(ContextType.Guild)]
public sealed class GameNewsModule(ISender sender) : InteractionModuleBase<SocketInteractionContext>
{
    private bool IsAdmin => Context.User is SocketGuildUser user && user.GuildPermissions.Administrator;

    [SlashCommand("list", "See existing subscriptions and their Steam App IDs.")]
    public async Task ListAsync([MinValue(1)] int page = 1)
    {
        await DeferAsync(ephemeral: true);
        var items = (await sender.Send(new GetGameNewsQuery(Context.Guild.Id))).Value;
        var lines = items.Skip((page - 1) * 10).Take(10).Select(item =>
            $"**{Format.Sanitize(item.Name)}** · App ID `{item.AppId}` · " +
            (item.Deleting ? "Deletion pending" : $"<#{item.ChannelId}> · {Sources(item.Sources)}"));
        string result = string.Join("\n", lines);
        await FollowupAsync(result.Length > 0 ? result : "No subscriptions on this page.", allowedMentions: AllowedMentions.None, ephemeral: true);
    }

    [SlashCommand("create", "Create a game news channel and role, and subscribe to it.")]
    public Task CreateAsync([Summary(description: "Steam App ID or Steam store link.")] string game,
        [Summary(description: "Game name displayed in the subscription.")] string name) =>
        RunAsync(new CreateGameNewsCommand(Context.Guild.Id, Context.User.Id, GameNewsService.ParseAppId(game), name),
            _ => "Subscription created. You’ll receive its game news pings.");

    [SlashCommand("subscribe", "Join the notification role for an existing game.")]
    public Task SubscribeAsync([Autocomplete<GameNewsAutocomplete>] string game) =>
        RunAsync(new SubscribeGameNewsCommand(Context.Guild.Id, Context.User.Id, GameNewsService.ParseAppId(game), true), _ => "Subscribed.");

    [SlashCommand("unsubscribe", "Remove your game news notification role.")]
    public Task UnsubscribeAsync([Autocomplete<GameNewsAutocomplete>] string game) =>
        RunAsync(new SubscribeGameNewsCommand(Context.Guild.Id, Context.User.Id, GameNewsService.ParseAppId(game), false), _ => "Unsubscribed.");

    [SlashCommand("delete", "Delete a subscription you created, including its channel and role.")]
    public Task DeleteAsync([Autocomplete<GameNewsAutocomplete>] string game) =>
        RunAsync(new DeleteGameNewsCommand(Context.Guild.Id, Context.User.Id, IsAdmin, GameNewsService.ParseAppId(game)), _ => "Subscription deleted.");

    [SlashCommand("sources", "Choose which kinds of Steam news a subscription posts.")]
    public Task SourcesAsync([Autocomplete<GameNewsAutocomplete>] string game,
        [Summary("patch-notes", "Updates and fixes from the developers.")] bool patchNotes,
        [Summary(description: "Events, sales and plans from the game's Steam page.")] bool announcements,
        [Summary(description: "Articles from gaming sites such as PC Gamer.")] bool press) =>
        RunAsync(new SetGameNewsSourcesCommand(Context.Guild.Id, Context.User.Id, IsAdmin, GameNewsService.ParseAppId(game),
                (patchNotes ? GameNewsSources.PatchNotes : 0) | (announcements ? GameNewsSources.Announcements : 0) | (press ? GameNewsSources.Press : 0)),
            item => $"**{Format.Sanitize(item.Name)}** now posts: {Sources(item.Sources)}.");

    private async Task RunAsync(IRequest<HappyMix.Domain.Abstractions.Result<GameNewsSubscription>> command, Func<GameNewsSubscription, string> success)
    {
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(command);
        await FollowupAsync(result.IsSuccess ? success(result.Value) : result.Error.Description, allowedMentions: AllowedMentions.None, ephemeral: true);
    }

    private static string Sources(GameNewsSources sources) => string.Join(", ", new[]
    {
        sources.HasFlag(GameNewsSources.PatchNotes) ? "patch notes" : null,
        sources.HasFlag(GameNewsSources.Announcements) ? "announcements" : null,
        sources.HasFlag(GameNewsSources.Press) ? "press" : null
    }.OfType<string>());
}

public sealed class GameNewsAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.Guild is null)
            return AutocompletionResult.FromSuccess();
        string query = autocompleteInteraction.Data.Current.Value as string ?? "";
        var items = (await services.GetRequiredService<ISender>().Send(new GetGameNewsQuery(context.Guild.Id))).Value;
        return AutocompletionResult.FromSuccess(items.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal))
            .Take(25).Select(item => new AutocompleteResult($"{item.Name} ({item.AppId})",
                item.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
