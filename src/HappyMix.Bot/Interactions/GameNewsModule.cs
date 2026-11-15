using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Bot.Services;

namespace HappyMix.Bot.Interactions;

[Group("game-news", "Follow Steam announcements for games in this server.")]
[RequireContext(ContextType.Guild)]
public sealed class GameNewsModule(GameNewsService news) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("list", "See existing subscriptions and their Steam App IDs.")]
    public async Task ListAsync([MinValue(1)] int page = 1)
    {
        await DeferAsync(ephemeral: true);
        var items = await news.ListAsync(Context.Guild.Id);
        var lines = items.Skip((page - 1) * 10).Take(10).Select(item =>
            $"**{Format.Sanitize(item.Name)}** · App ID `{item.AppId}` · " +
            (item.Deleting ? "Deletion pending" : $"<#{item.ChannelId}>"));
        string result = string.Join("\n", lines);
        await FollowupAsync(result.Length > 0 ? result : "No subscriptions on this page.", allowedMentions: AllowedMentions.None, ephemeral: true);
    }

    [SlashCommand("create", "Create a game news channel and role, and subscribe to it.")]
    public Task CreateAsync([Summary(description: "Steam App ID or Steam store link.")] string game,
        [Summary(description: "Game name displayed in the subscription.")] string name) => ChangeAsync(game, "create", name);

    [SlashCommand("subscribe", "Join the notification role for an existing game.")]
    public Task SubscribeAsync([Autocomplete<GameNewsAutocomplete>] string game) => ChangeAsync(game, "subscribe");

    [SlashCommand("unsubscribe", "Remove your game news notification role.")]
    public Task UnsubscribeAsync([Autocomplete<GameNewsAutocomplete>] string game) => ChangeAsync(game, "unsubscribe");

    [SlashCommand("delete", "Delete a subscription you created, including its channel and role.")]
    public Task DeleteAsync([Autocomplete<GameNewsAutocomplete>] string game) => ChangeAsync(game, "delete");

    private async Task ChangeAsync(string game, string action, string? name = null)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            await news.ChangeAsync(Context.Guild, (IGuildUser)Context.User, GameNewsService.ParseAppId(game), action, name);
            await FollowupAsync(action switch
            {
                "create" => "Subscription created. You’ll receive its game news pings.",
                "delete" => "Subscription deleted.",
                "subscribe" => "Subscribed.",
                _ => "Unsubscribed."
            }, ephemeral: true);
        }
        catch (InvalidOperationException error)
        {
            await FollowupAsync(error.Message, ephemeral: true);
        }
        catch (HttpException)
        {
            await FollowupAsync("Discord could not finish the request. Check the bot’s permissions and role position, then try again.", ephemeral: true);
        }
    }
}

public sealed class GameNewsAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var news = services.GetRequiredService<GameNewsService>();
        if (context.Guild is null)
            return AutocompletionResult.FromSuccess();
        string query = autocompleteInteraction.Data.Current.Value as string ?? "";
        var items = await news.ListAsync(context.Guild.Id);
        return AutocompletionResult.FromSuccess(items.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal))
            .Take(25).Select(item => new AutocompleteResult($"{item.Name} ({item.AppId})",
                item.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
