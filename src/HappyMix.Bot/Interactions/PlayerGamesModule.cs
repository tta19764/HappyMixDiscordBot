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

    [SlashCommand("stats", "Show Dota 2 and CS2 stats: yours, or a member's who shares them.")]
    public async Task StatsAsync([Summary(description: "Someone else; their stats show only if they share them.")] IUser? member = null)
    {
        await DeferAsync(ephemeral: true);
        var target = member ?? Context.User;
        bool self = target.Id == Context.User.Id;
        var stats = (await sender.Send(new HappyMix.Application.Stats.GetMyStatsQuery(target.Id))).Value;
        if (!stats.Shares)
        {
            await Reply(self ? "Your stats aren't shared. Turn them on with /games share-stats, or on a Dota 2 or CS2 group's Stats tab."
                : $"{Format.Sanitize(target.GlobalName ?? target.Username)} doesn't share their game stats.");
            return;
        }
        await Reply(StatsText(target.GlobalName ?? target.Username, stats));
    }

    [SlashCommand("share-stats", "Show or hide your Dota 2 and CS2 stats in your groups.")]
    public async Task ShareStatsAsync([Summary(description: "On: groups see your stats. Off: hidden, and saved stats are deleted.")] bool share)
    {
        await DeferAsync(ephemeral: true);
        var stats = (await sender.Send(new HappyMix.Application.Stats.SetStatsSharingCommand(Context.User.Id, share))).Value;
        await Reply(!share ? "Your game stats are hidden and the saved stats are deleted."
            : stats.SteamId is null ? "Sharing is on. Link your Steam account with /games steam so HappyMix can find your stats."
            : "Sharing is on.\n" + StatsText(Context.User.GlobalName ?? Context.User.Username, stats));
    }

    /// <summary>The second Dota 2 line: last matches and streak, most played role, MMR estimate.</summary>
    internal static string DotaMore(HappyMix.Application.Abstractions.Stats.DotaProfile profile)
    {
        var parts = new List<string>();
        if (profile.Recent is { } recent)
            parts.Add($"last {recent.Matches} ranked {recent.Wins}W {recent.Matches - recent.Wins}L"
                + (Math.Abs(recent.Streak) >= 2 ? $", {Math.Abs(recent.Streak)} {(recent.Streak > 0 ? "wins" : "losses")} in a row" : ""));
        if (profile.Roles is [var top, ..] && profile.Roles.Sum(role => role.Games) is > 0 and var games)
            parts.Add($"mostly {Role(top.Key)} ({top.Games * 100 / games}%)");
        if (HappyMix.Application.Stats.GameStats.MmrEstimate(profile.Recent) is { } mmr)
            parts.Add($"≈ {(mmr >= 0 ? "+" : "−")}{Math.Abs(mmr)} MMR");
        return string.Join(" · ", parts);

        static string Role(string key) => key switch
        {
            "1" => "pos 1 carry", "2" => "pos 2 mid", "3" => "pos 3 offlane", "4" => "pos 4 support", "5" => "pos 5 hard support",
            "safe" => "safe lane", "off" => "offlane", "roam" => "roaming", _ => key
        };
    }

    /// <summary>A short card in plain text: Dota 2 medal and record, CS2 FACEIT level, and profile links.</summary>
    internal static string StatsText(string name, HappyMix.Application.Stats.MyStats stats)
    {
        var lines = new List<string> { $"**{Format.Sanitize(name)}**" };
        long? account = HappyMix.Application.Stats.GameStats.DotaAccountId(stats.SteamId);
        string dota = stats.Dota switch
        {
            null => "getting stats…",
            { Status: HappyMix.Application.Abstractions.Stats.StatsStatus.NoPublicData } => "no public match data (turn on Expose Public Match Data in Dota 2)",
            var cached when HappyMix.Application.Stats.GameStats.Read<HappyMix.Application.Abstractions.Stats.DotaProfile>(cached) is { } profile =>
                $"{Medal(profile.RankTier, profile.LeaderboardRank)} · last 30 days {profile.Wins}W {profile.Losses}L"
                + (profile.Wins + profile.Losses > 0 ? $" ({profile.Wins * 100 / (profile.Wins + profile.Losses)}%)" : "")
                + (profile.TopHeroes.Count > 0 ? " · " + string.Join(", ", profile.TopHeroes.Select(hero => $"{Format.Sanitize(hero.Name)} {hero.Games}")) : ""),
            _ => "OpenDota didn't answer; try again later"
        };
        lines.Add($"Dota 2: {dota}" + (account is { } id ? $" · <https://www.opendota.com/players/{id}> · <https://www.dotabuff.com/players/{id}>" : ""));
        if (HappyMix.Application.Stats.GameStats.Read<HappyMix.Application.Abstractions.Stats.DotaProfile>(stats.Dota) is { } more && DotaMore(more) is { Length: > 0 } extra)
            lines.Add("Dota 2: " + extra);
        if (stats.Cs2Enabled)
        {
            string cs2 = stats.Cs2 switch
            {
                null => "getting stats…",
                { Status: HappyMix.Application.Abstractions.Stats.StatsStatus.NoAccount } => "no FACEIT account",
                var cached when HappyMix.Application.Stats.GameStats.Read<HappyMix.Application.Abstractions.Stats.Cs2Profile>(cached) is { } profile =>
                    $"FACEIT level {profile.Level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}"
                    + (profile.Elo is { } elo ? $" · {elo} Elo" : "") + $" · {profile.Matches} matches"
                    + (profile.KillDeath is { } kd ? $" · K/D {kd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}" : ""),
                _ => "FACEIT didn't answer; try again later"
            };
            lines.Add($"CS2: {cs2}" + (stats.SteamId is { } steamId ? $" · <https://leetify.com/app/profile/{steamId}>" : ""));
        }
        return string.Join("\n", lines);
    }

    private static readonly string[] Medals = ["Herald", "Guardian", "Crusader", "Archon", "Legend", "Ancient", "Divine", "Immortal"];

    private static string Medal(int? rankTier, int? leaderboard) => HappyMix.Application.Stats.GameStats.DotaMedal(rankTier) is var (medal, stars)
        ? Medals[medal - 1] + (medal < 8 && stars > 0 ? $" {stars}" : "") + (leaderboard is { } place ? $" #{place}" : "")
        : "Unranked";

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
