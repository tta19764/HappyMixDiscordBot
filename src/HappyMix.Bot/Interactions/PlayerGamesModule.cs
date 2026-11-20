using Discord;
using Discord.Interactions;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Players;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Bot.Interactions;

/// <summary>
/// /games: the same game list and Want / Can / Won't marks as the dashboard's My games page. Steam sign-in and using the
/// Steam account connected to Discord need a browser, so they stay on the dashboard.
/// </summary>
[Group("games", "Your games and how much you want to play them.")]
public sealed class PlayerGamesModule(ISender sender) : InteractionModuleBase<SocketInteractionContext>
{
    public enum Mark
    {
        [ChoiceDisplay("Want to play")] Want = 1,
        [ChoiceDisplay("Can play")] Can = 2,
        [ChoiceDisplay("Won't play")] Wont = 3
    }

    [SlashCommand("list", "Show your games and their marks.")]
    public async Task ListAsync([Summary(description: "Only games with this mark.")] Mark? mark = null, [MinValue(1)] int page = 1)
    {
        await DeferAsync(ephemeral: true);
        var data = (await sender.Send(new GetMyGamesQuery(Context.User.Id))).Value;
        var games = data.Games.Where(game => mark is null || game.Preference == (GamePreference)mark)
            .OrderBy(game => game.Preference).ThenByDescending(game => game.RecentMinutes ?? 0).ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        int pages = Math.Max(1, (int)Math.Ceiling(games.Length / 20.0));
        if (games.Length == 0)
        {
            await Reply(data.Games.Count == 0 ? "Your list is empty. Use /games mark, /games steam and /games import, or the dashboard's My games page." : "No games with that mark.");
            return;
        }
        var lines = games.Skip((Math.Min(page, pages) - 1) * 20).Take(20).Select(game =>
            $"{Label(game.Preference)} · **{Format.Sanitize(game.Name)}**" + (game.PlaytimeMinutes is > 0 and var minutes ? $" · {Math.Max(1, minutes / 60)} h" : ""));
        string steam = data.Steam is { } account ? $"Steam: `{account.SteamId}`{(account.Verified ? " (verified)" : "")}" : "Steam: not linked";
        await Reply($"{steam}\n{string.Join("\n", lines)}" + (pages > 1 ? $"\nPage {Math.Min(page, pages)} of {pages}" : ""));
    }

    [SlashCommand("mark", "Add a game to your list or change its mark.")]
    public async Task MarkAsync([Autocomplete<PlayerGameAutocomplete>, Summary(description: "A game from your list, a search result, or any name.")] string game, Mark mark)
    {
        await DeferAsync(ephemeral: true);
        var preference = (GamePreference)mark;
        var own = (await sender.Send(new GetMyGamesQuery(Context.User.Id))).Value.Games.FirstOrDefault(item => item.Key == game);
        var result = own is not null
            ? await sender.Send(new SetGamePreferenceCommand(Context.User.Id, own.Key, preference))
            : PlayerGameAutocomplete.ParseSearchValue(game) is { } picked
                ? await sender.Send(new AddPlayerGameCommand(Context.User.Id, picked.Name, null, picked.Provider, picked.Id, preference))
                : await sender.Send(new AddPlayerGameCommand(Context.User.Id, game, null, null, null, preference));
        await Reply(result.IsSuccess ? $"**{Format.Sanitize(result.Value.Name)}**: {Label(result.Value.Preference)}." : result.Error.Description);
    }

    [SlashCommand("remove", "Remove a game from your list.")]
    public async Task RemoveAsync([Autocomplete<PlayerGameAutocomplete>] string game)
    {
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new RemovePlayerGameCommand(Context.User.Id, game));
        await Reply(result.IsSuccess ? "Game removed." : result.Error.Description);
    }

    [SlashCommand("steam", "Link your Steam profile by its link, custom name or SteamID64.")]
    public async Task SteamAsync([Summary(description: "steamcommunity.com/id/… or /profiles/…, or a SteamID64.")] string profile)
    {
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new LinkSteamCommand(Context.User.Id, profile));
        await Reply(result.IsSuccess
            ? $"Steam linked: `{result.Value.SteamId}`. Use /games import to bring in your library; sign in with Steam on the dashboard to verify it."
            : result.Error.Description);
    }

    [SlashCommand("import", "Import your public Steam library. Marks you already set are kept.")]
    public async Task ImportAsync()
    {
        await DeferAsync(ephemeral: true);
        var result = await sender.Send(new ImportSteamGamesCommand(Context.User.Id));
        await Reply(result.IsSuccess ? $"Library imported: {result.Value.Added} new, {result.Value.Updated} updated." : result.Error.Description);
    }

    private static string Label(GamePreference preference) => preference switch
    {
        GamePreference.Want => "Want to play", GamePreference.Can => "Can play", _ => "Won't play"
    };

    private Task<IUserMessage> Reply(string text) => FollowupAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
}

/// <summary>Your own games first, then catalog search results (any platform) for names you haven't added yet.</summary>
public sealed class PlayerGameAutocomplete : AutocompleteHandler
{
    private const string SearchPrefix = "add|";

    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        string query = (autocompleteInteraction.Data.Current.Value as string ?? "").Trim();
        var sender = services.GetRequiredService<ISender>();
        var mine = (await sender.Send(new GetMyGamesQuery(context.User.Id))).Value.Games;
        var results = mine.Where(game => game.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).Take(15)
            .Select(game => new AutocompleteResult(Trim($"{game.Name} · on your list"), game.Key)).ToList();
        if (query.Length >= 3 && parameter.Command.Name == "mark")
        {
            // Discord gives autocomplete about three seconds; a slow catalog just means no search results this time.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var found = await services.GetRequiredService<IMediaCatalog>().SearchAsync("game", query, timeout.Token);
                results.AddRange(found
                    .Where(item => !mine.Any(game => GameKeys.Same(game, item.Title, item.SteamAppId is null ? item.Source : "steam", item.SteamAppId ?? item.ExternalId)))
                    .Take(25 - results.Count)
                    .Select(item => new AutocompleteResult(Trim(item.Title),
                        Trim($"{SearchPrefix}{(item.SteamAppId is { } steam ? "steam|" + steam : item.Source + "|" + item.ExternalId)}|{item.Title}"))));
            }
            catch (OperationCanceledException) { }
        }
        return AutocompletionResult.FromSuccess(results.Take(25));
    }

    /// <summary>A picked search result: "add|steam|730|Counter-Strike 2".</summary>
    public static (string Provider, string Id, string Name)? ParseSearchValue(string value) =>
        value.StartsWith(SearchPrefix, StringComparison.Ordinal) && value[SearchPrefix.Length..].Split('|', 3) is [var provider, var id, var name]
            && provider is "steam" or "igdb" && name.Length > 0
            ? (provider, id, name) : null;

    private static string Trim(string text) => text.Length > 100 ? text[..100] : text;
}
