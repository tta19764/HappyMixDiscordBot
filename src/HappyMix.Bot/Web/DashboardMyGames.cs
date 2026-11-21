using System.Globalization;
using System.Security.Cryptography;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Players;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <param name="DiscordSteam">The Steam account connected to the viewer's Discord profile, when sign-in could read it.</param>
/// <param name="ConnectionsChecked">Whether sign-in asked Discord for connections at all (older sessions didn't).</param>
public sealed record MyGamesPage(ulong GuildId, string GuildName, bool CanConfigure, MyGames Data, (string Id, string Name, bool Verified)? DiscordSteam,
    bool ConnectionsChecked, string Mark, string? Search, int Page, BannerModel? Banner, string Sort = "mark", DateTimeOffset? SyncedAt = null)
{
    /// <summary>List orders: by mark (the default), hours played, A–Z or recently played.</summary>
    public static readonly string[] Sorts = ["mark", "hours", "name", "recent"];

    /// <summary>The cover from Steam's image servers for Steam games; other games show a placeholder.</summary>
    public static string? Art(PlayerGame game) => game.Provider == "steam" && game.ExternalId is { Length: > 0 } appId && appId.All(char.IsAsciiDigit)
        ? $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg"
        : null;

    public const int PageSize = 50;
    public string Url => $"/games/{GuildId}";

    public IReadOnlyList<PlayerGame> Matching => Data.Games
        .Where(game => Mark switch { "want" => game.Preference == GamePreference.Want, "can" => game.Preference == GamePreference.Can,
            "wont" => game.Preference == GamePreference.Wont, _ => true })
        .Where(game => Search is null || game.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase))
        .Sorted(Sort).ToArray();

    public int Count(GamePreference? preference) => preference is { } value ? Data.Games.Count(game => game.Preference == value) : Data.Games.Count;

    public string Link(string? mark = null, int? page = null, string? search = null, string? sort = null)
    {
        var query = new List<string>();
        string m = mark ?? Mark;
        if (m != "all") query.Add("mark=" + m);
        if ((sort ?? Sort) is var order && order != "mark") query.Add("sort=" + order);
        if ((search ?? Search) is { Length: > 0 } q) query.Add("q=" + Uri.EscapeDataString(q));
        if ((page ?? 1) > 1) query.Add("page=" + (page ?? 1).ToString(CultureInfo.InvariantCulture));
        return Url + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }
}

internal static class PlayerGameOrder
{
    public static IEnumerable<PlayerGame> Sorted(this IEnumerable<PlayerGame> games, string sort)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        return sort switch
        {
            "hours" => games.OrderByDescending(game => game.PlaytimeMinutes ?? 0).ThenBy(game => game.Name, byName),
            "name" => games.OrderBy(game => game.Name, byName),
            "recent" => games.OrderByDescending(game => game.RecentMinutes ?? 0).ThenByDescending(game => game.PlaytimeMinutes ?? 0)
                .ThenBy(game => game.Name, byName),
            _ => games.OrderBy(game => game.Preference).ThenByDescending(game => game.RecentMinutes ?? 0)
                .ThenByDescending(game => game.PlaytimeMinutes ?? 0).ThenBy(game => game.Name, byName)
        };
    }
}

/// <summary>My games: the viewer's Steam link, their game list and their Want / Can / Won't marks.</summary>
public sealed partial class DashboardController
{
    private const string SteamStateCookie = "hm_steam_state";

    [HttpGet("/games/{guildId}")]
    public async Task<IActionResult> MyGames(ulong guildId, string? mark, string? q, string? sort, int page = 1)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();
        RememberServer(guildId);
        var data = (await sender.Send(new GetMyGamesQuery(actor.UserId), HttpContext.RequestAborted)).Value;
        string filter = mark is "want" or "can" or "wont" ? mark : "all";
        string? search = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(60, q.Trim().Length)];
        return View("MyGames", new MyGamesPage(guildId, GuildName(guildId), viewer.CanConfigure, data, DiscordConnections.FromUser(User),
            DiscordConnections.Checked(User), filter, search, Math.Max(1, page), TakeGamesBanner(),
            MyGamesPage.Sorts.Contains(sort) ? sort! : "mark", DiscordProfileClaims.SyncedAt(User)));
    }

    [HttpPost("/games/{guildId}/steam")]
    public Task<IActionResult> LinkSteam(ulong guildId, string? steam) =>
        GamesActionAsync(guildId, userId => sender.Send(new LinkSteamCommand(userId, steam ?? ""), HttpContext.RequestAborted),
            account => text["Games_SteamLinked"].Value);

    [HttpPost("/games/{guildId}/steam/discord")]
    public Task<IActionResult> LinkDiscordSteam(ulong guildId) =>
        GamesActionAsync(guildId, async userId => DiscordConnections.FromUser(User) is { } connected
                ? await sender.Send(new LinkDiscordSteamCommand(userId, connected.Id, connected.Verified), HttpContext.RequestAborted)
                : HappyMix.Domain.Abstractions.Result.Failure<SteamAccount>(new("Players.NoDiscordSteam", "Your Discord profile has no Steam connection.")),
            account => text["Games_SteamLinked"].Value);

    /// <summary>Starts Steam sign-in. A one-time state value in a cookie ties the answer to this browser.</summary>
    [HttpGet("/games/{guildId}/steam/signin")]
    public async Task<IActionResult> SteamSignIn(ulong guildId)
    {
        if (await GetDiscordMemberAsync(guildId) is null)
            return NotMember();
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append(SteamStateCookie, state, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps, MaxAge = TimeSpan.FromMinutes(10), Path = $"/games/{guildId}"
        });
        string origin = $"{Request.Scheme}://{Request.Host}";
        return Redirect(SteamIds.SignInUrl($"{origin}/games/{guildId}/steam/return?state={state}", origin + "/"));
    }

    [HttpGet("/games/{guildId}/steam/return")]
    public async Task<IActionResult> SteamSignInReturn(ulong guildId, string? state)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        string? expected = Request.Cookies[SteamStateCookie];
        Response.Cookies.Delete(SteamStateCookie, new CookieOptions { Path = $"/games/{guildId}" });
        if (expected is null || state is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(state)))
        {
            SetGamesBanner("error", text["That_didn_t_work"].Value, text.Message("Steam didn't confirm the sign-in. Try again.").Value);
            return Redirect($"/games/{guildId}");
        }
        var response = Request.Query.Where(pair => pair.Key.StartsWith("openid.", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        string returnTo = $"{Request.Scheme}://{Request.Host}/games/{guildId}/steam/return";
        var result = await sender.Send(new VerifySteamCommand(member.Id, response, returnTo), HttpContext.RequestAborted);
        SetGamesBanner(result.IsSuccess ? "success" : "error", result.IsSuccess ? text["Games_SteamVerified"].Value : text["That_didn_t_work"].Value,
            result.IsFailure ? text.Message(result.Error.Description).Value : null);
        return Redirect($"/games/{guildId}");
    }

    [HttpPost("/games/{guildId}/steam/unlink")]
    public Task<IActionResult> UnlinkSteam(ulong guildId) =>
        GamesActionAsync(guildId, async userId =>
        {
            var result = await sender.Send(new UnlinkSteamCommand(userId), HttpContext.RequestAborted);
            return result.IsSuccess ? HappyMix.Domain.Abstractions.Result.Success(true) : HappyMix.Domain.Abstractions.Result.Failure<bool>(result.Error);
        }, _ => text["Games_SteamUnlinked"].Value);

    [HttpPost("/games/{guildId}/import")]
    public Task<IActionResult> ImportSteamGames(ulong guildId) =>
        GamesActionAsync(guildId, userId => sender.Send(new ImportSteamGamesCommand(userId), HttpContext.RequestAborted),
            import => text["Games_Imported", import.Added, import.Updated].Value);

    [HttpPost("/games/{guildId}/add")]
    public Task<IActionResult> AddPlayerGame(ulong guildId, string? name, string? steam, string? provider, string? externalId, int preference = 1) =>
        GamesActionAsync(guildId, userId => sender.Send(new AddPlayerGameCommand(userId, name ?? "", steam, provider, externalId, (GamePreference)preference),
            HttpContext.RequestAborted), game => text["Games_Added", game.Name].Value);

    [HttpPost("/games/{guildId}/mark")]
    public Task<IActionResult> MarkPlayerGame(ulong guildId, string? key, int preference, string? returnUrl) =>
        GamesActionAsync(guildId, userId => sender.Send(new SetGamePreferenceCommand(userId, key ?? "", (GamePreference)preference), HttpContext.RequestAborted),
            game => text["Games_Marked", game.Name].Value, returnUrl);

    /// <summary>
    /// The Save button: each listed game posts <c>k{i}</c> (its key) and <c>m{i}</c> (the chosen mark), so one save
    /// covers the whole page and nothing changes until it is pressed.
    /// </summary>
    [HttpPost("/games/{guildId}/marks")]
    public Task<IActionResult> SavePlayerGameMarks(ulong guildId, string? returnUrl)
    {
        var marks = new Dictionary<string, GamePreference>(StringComparer.Ordinal);
        foreach (var (name, value) in Request.Form)
            if (name.Length > 1 && name[0] == 'k' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                && value.ToString() is { Length: > 0 } key && marks.Count < MyGamesPage.PageSize
                && int.TryParse(Request.Form["m" + index.ToString(CultureInfo.InvariantCulture)], NumberStyles.None, CultureInfo.InvariantCulture, out int mark))
                marks[key] = (GamePreference)mark;
        return GamesActionAsync(guildId, userId => sender.Send(new SetGamePreferencesCommand(userId, marks), HttpContext.RequestAborted),
            changed => changed == 0 ? text["Games_NothingChanged"].Value : text["Games_MarksSaved", changed].Value, returnUrl);
    }

    [HttpPost("/games/{guildId}/remove")]
    public Task<IActionResult> RemovePlayerGame(ulong guildId, string? key, string? returnUrl) =>
        GamesActionAsync(guildId, async userId =>
        {
            var result = await sender.Send(new RemovePlayerGameCommand(userId, key ?? ""), HttpContext.RequestAborted);
            return result.IsSuccess ? HappyMix.Domain.Abstractions.Result.Success(true) : HappyMix.Domain.Abstractions.Result.Failure<bool>(result.Error);
        }, _ => text["Games_Removed"].Value, returnUrl);

    private async Task<IActionResult> GamesActionAsync<T>(ulong guildId, Func<ulong, Task<HappyMix.Domain.Abstractions.Result<T>>> action,
        Func<T, string> success, string? returnUrl = null)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        var result = await action(member.Id);
        SetGamesBanner(result.IsSuccess ? "success" : "error", result.IsSuccess ? success(result.Value) : text["That_didn_t_work"].Value,
            result.IsFailure ? text.Message(result.Error.Description).Value : null);
        string fallback = $"/games/{guildId}";
        return Redirect(returnUrl is { Length: > 0 } back && Url.IsLocalUrl(back) && back.StartsWith(fallback, StringComparison.Ordinal) ? back : fallback);
    }

    private void SetGamesBanner(string kind, string title, string? body)
    {
        TempData["GamesBannerKind"] = kind;
        TempData["GamesBannerTitle"] = title;
        TempData["GamesBannerText"] = body;
    }

    private BannerModel? TakeGamesBanner() => TempData["GamesBannerTitle"] is string title
        ? new BannerModel(TempData["GamesBannerKind"] as string ?? "success", title, TempData["GamesBannerText"] as string)
        : null;
}
