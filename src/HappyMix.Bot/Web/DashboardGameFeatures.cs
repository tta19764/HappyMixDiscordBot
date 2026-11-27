using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Phasmophobia;
using HappyMix.Application.Stats;
using HappyMix.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>
/// A game with features on the Game features pages: its address key, Steam app and the features it has.
/// A <paramref name="Placeholder"/> game has no features yet: its card and page say they are coming.
/// </summary>
public sealed record GameFeatureInfo(string Key, string Name, string AppId, string[] Features, bool Placeholder = false)
{
    public string Art => SteamArt.Header(AppId);
}

/// <summary>The games that have features. New games are added here and get a card and a page of their own.</summary>
public static class GameFeatureCatalog
{
    public static readonly GameFeatureInfo Dota2 = new("dota-2", "Dota 2", GameStats.DotaAppId, ["stats", "meta", "recaps", "digest"]);
    public static readonly GameFeatureInfo Cs2 = new("cs2", "Counter-Strike 2", GameStats.Cs2AppId, ["stats", "recaps", "digest"]);
    public static readonly GameFeatureInfo Phasmophobia = new("phasmophobia", "Phasmophobia", "739630", ["ghosts", "finder"]);
    public static readonly IReadOnlyList<GameFeatureInfo> All = [Dota2, Cs2, Phasmophobia];

    public static GameFeatureInfo? Find(string? key) => All.FirstOrDefault(game => string.Equals(game.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One game card: what is set up for the viewer and the server.</summary>
public sealed record GameCardModel(GameFeatureInfo Game, IReadOnlyList<string> Status);

/// <summary>The Game features list: games with features, searchable, as cards, a table or a compact list.</summary>
public sealed record GamesPage(ulong GuildId, string GuildName, bool IsAdmin, IReadOnlyList<GameCardModel> Games, string? Query, string View, BannerModel? Banner)
{
    public static readonly string[] Views = ["cards", "table", "list"];
}

/// <summary>One game's features page, divided into tabs.</summary>
/// <param name="DefaultTab">The tab shown when the address names none: the meta when a search was made, otherwise your stats.</param>
public sealed record GameFeaturesPage(ulong GuildId, string GuildName, bool IsAdmin, GameFeatureInfo Game, GameFeatures Data,
    IReadOnlyList<(ulong Id, string Name)> Channels, string TimeZone, BannerModel? Banner, string DefaultTab = "stats")
{
    public string Url => $"/game-features/{GuildId}/{Game.Key}";
}

/// <summary>
/// The Phasmophobia page: the ghost database and the evidence finder. Each ghost carries, as JSON, every evidence set a
/// contract can show for it (see <see cref="EvidenceFinder.ShownSets"/>), so the page filters instantly with the
/// server's rules instead of a copy of them in JavaScript.
/// </summary>
public sealed record PhasmophobiaPage(ulong GuildId, string GuildName, bool IsAdmin, GameFeatureInfo Game, IReadOnlyList<Ghost> Ghosts)
{
    public string Url => $"/game-features/{GuildId}/{Game.Key}";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// The sets keyed by evidence number ("0"–"3"), each with forced evidence kept ("on") and not ("off"):
    /// <c>{"2":{"on":[["dots","uv"]],"off":[...]}}</c>.
    /// </summary>
    public static string Sets(Ghost ghost) => System.Text.Json.JsonSerializer.Serialize(
        Enumerable.Range(0, 4).ToDictionary(count => count.ToString(System.Globalization.CultureInfo.InvariantCulture), count => new
        {
            on = EvidenceFinder.ShownSets(ghost, new FinderInput(new Dictionary<string, EvidenceMark>(), count)).Select(Order).ToArray(),
            off = EvidenceFinder.ShownSets(ghost, new FinderInput(new Dictionary<string, EvidenceMark>(), count, ForcedEvidence: false)).Select(Order).ToArray()
        }), Json);

    private static string[] Order(IEnumerable<string> set) => set.OrderBy(id => id, StringComparer.Ordinal).ToArray();
}

/// <summary>The viewer's own stat cards with the sharing switch and Refresh, as on the Game features pages.</summary>
/// <param name="Only">Show only this game's card (each game's page shows its own).</param>
/// <summary>The extra Dota 2 lines on a stats card: role, medal change, form, averages and, on a group, best teammates.</summary>
public sealed record DotaMoreModel(DotaProfile Profile, DateTimeOffset Now, IReadOnlyList<(string Name, int Games, int Wins)> Teammates);

/// <summary>
/// The stats sharing form: a switch, or with several linked Steam accounts one box per account (none checked = off).
/// </summary>
public sealed record StatsShareModel(string Action, bool Shares, IReadOnlyList<HappyMix.Application.Abstractions.Players.SteamAccount> Accounts,
    string? ReturnAnchor = null);

public sealed record OwnStatsModel(ulong GuildId, MyStats Mine, string ShareUrl, string RefreshUrl, DateTimeOffset Now, StatsGame? Only = null);

public sealed partial class DashboardController
{
    private const string FeaturesBanner = "FeaturesBanner";
    private const string GamesViewCookie = "hm_games_view";

    [HttpGet("/game-features/{guildId}")]
    public async Task<IActionResult> GameFeatures(ulong guildId, string? q, string? view)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        // Meta searches used to live here: send old links to the Dota 2 page with the same filters.
        if (Request.Query.ContainsKey("patch") || Request.Query.ContainsKey("role") || Request.Query.ContainsKey("rank"))
            return Redirect($"/game-features/{guildId}/{GameFeatureCatalog.Dota2.Key}{Request.QueryString}#meta");
        RememberServer(guildId);

        if (view is not null && GamesPage.Views.Contains(view))
            Response.Cookies.Append(GamesViewCookie, view, new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1), Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/"
            });
        string chosen = view is not null && GamesPage.Views.Contains(view) ? view
            : Request.Cookies[GamesViewCookie] is { } saved && GamesPage.Views.Contains(saved) ? saved : "cards";

        var mine = (await sender.Send(new GetMyStatsQuery(member.Id), HttpContext.RequestAborted)).Value;
        var meta = (await sender.Send(new GetDotaMetaSettingsQuery(guildId), HttpContext.RequestAborted)).Value;
        string search = q?.Trim() ?? "";
        var cards = GameFeatureCatalog.All
            .Where(game => search.Length == 0 || game.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Select(game => new GameCardModel(game, Status(game, mine, meta)))
            .ToArray();
        return View("GameFeatures", new GamesPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator, cards,
            search.Length == 0 ? null : search, chosen, TakeFeaturesBanner()));
    }

    [HttpGet("/game-features/{guildId}/{game}")]
    public async Task<IActionResult> GameFeaturePage(ulong guildId, string game, MetaPatch? patch, int? role, MetaRank? rank, MetaSort? sort, double? minPick)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        if (GameFeatureCatalog.Find(game) is not { } info)
            return NotFound();
        RememberServer(guildId);
        if (info == GameFeatureCatalog.Phasmophobia)
            return View("Phasmophobia", new PhasmophobiaPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator, info,
                GhostDatabase.For(System.Globalization.CultureInfo.CurrentUICulture)));

        GameFeatures data;
        bool searched = patch is not null || role is not null || rank is not null || sort is not null || minPick is not null;
        if (info == GameFeatureCatalog.Dota2)
        {
            // The search runs only from its Search button (a GET with filters); otherwise the saved post filters are shown.
            MetaFilter? filter = !searched ? null
                : new MetaFilter(patch ?? MetaPatch.Current, role is >= 0 and <= 5 ? role.Value : 0, rank ?? MetaRank.All, sort ?? MetaSort.Score,
                    minPick is { } pick && DotaMetas.MinPickRates.Contains(pick) ? pick : 1);
            data = (await sender.Send(new GetGameFeaturesQuery(guildId, member.Id, filter), HttpContext.RequestAborted)).Value;
        }
        else
        {
            var mine = (await sender.Send(new GetMyStatsQuery(member.Id), HttpContext.RequestAborted)).Value;
            data = new GameFeatures(mine, DotaMetaSettings.Default(guildId), null, null, clock.UtcNow);
        }
        var channels = discord.GetGuild(guildId)?.TextChannels.OrderBy(channel => channel.Position).Select(channel => (channel.Id, channel.Name)).ToArray()
            ?? [];
        return View("GameFeature", new GameFeaturesPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator, info, data, channels,
            (await GetGuildZoneAsync(guildId, HttpContext.RequestAborted)).Id, TakeFeaturesBanner(), searched ? "meta" : "stats"));
    }

    [HttpPost("/game-features/{guildId}/{game}/stats/share")]
    public async Task<IActionResult> ShareStatsFromFeatures(ulong guildId, string game, bool share, bool pick, string[]? accounts)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        var mine = await ShareStatsAsync(member.Id, guildId, share, pick, accounts);
        return FeaturesDone(guildId, game, "stats", text[mine.Shares ? "Stats_Shared" : "Stats_Unshared"].Value);
    }

    [HttpPost("/game-features/{guildId}/{game}/stats/refresh")]
    public async Task<IActionResult> RefreshStatsFromFeatures(ulong guildId, string game)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        await sender.Send(new RefreshMyStatsCommand(member.Id, guildId), HttpContext.RequestAborted);
        return FeaturesDone(guildId, game, "stats", text["Stats_Refreshed"].Value);
    }

    [HttpPost("/game-features/{guildId}/dota-meta")]
    public async Task<IActionResult> SaveDotaMeta(ulong guildId, MetaSchedule schedule, ulong? channel, DayOfWeek day, int hour, int count = DotaMetas.DefaultCount,
        MetaRank postRank = MetaRank.All, MetaPatch postPatch = MetaPatch.Current, MetaSort postSort = MetaSort.Score, double postMinPick = 1)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        if (!member.GuildPermissions.Administrator)
            return Forbid();
        var result = await sender.Send(new SaveDotaMetaCommand(guildId, schedule, channel is > 0 ? channel : null, day, hour, count, postRank, postPatch,
            postSort, postMinPick), HttpContext.RequestAborted);
        return result.IsSuccess
            ? FeaturesDone(guildId, GameFeatureCatalog.Dota2.Key, "posting", text["Meta_Saved"].Value)
            : FeaturesDone(guildId, GameFeatureCatalog.Dota2.Key, "posting", text["That_didn_t_work"].Value, text.Message(result.Error.Description).Value, failed: true);
    }

    /// <summary>Creates a #dota-meta channel under "Bot Notifications" and makes it the meta channel.</summary>
    [HttpPost("/game-features/{guildId}/dota-meta/channel")]
    public async Task<IActionResult> CreateDotaMetaChannel(ulong guildId, [FromServices] NotificationChannels channels)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        if (!member.GuildPermissions.Administrator)
            return Forbid();
        string dota = GameFeatureCatalog.Dota2.Key;
        if (discord.GetGuild(guildId) is not { } guild)
            return FeaturesDone(guildId, dota, "posting", text["That_didn_t_work"].Value, text["Meta_NoGuild"].Value, failed: true);
        try
        {
            var created = await channels.CreateAsync(guild, "dota-meta", text["Meta_ChannelTopic"].Value);
            var saved = (await sender.Send(new GetDotaMetaSettingsQuery(guildId), HttpContext.RequestAborted)).Value;
            await sender.Send(new SaveDotaMetaCommand(guildId, saved.Schedule, created.Id, saved.Day, saved.Hour, saved.Count, saved.Rank, saved.Patch,
                saved.Sort, saved.MinPickRate), HttpContext.RequestAborted);
            return FeaturesDone(guildId, dota, "posting", text["Meta_ChannelCreated", "#" + created.Name].Value);
        }
        catch (Discord.Net.HttpException error)
        {
            return FeaturesDone(guildId, dota, "posting", text["That_didn_t_work"].Value,
                text["Meta_ChannelFailed", error.DiscordCode?.ToString() ?? error.HttpCode.ToString()].Value, failed: true);
        }
    }

    [HttpPost("/game-features/{guildId}/dota-meta/post")]
    public async Task<IActionResult> PostDotaMetaNow(ulong guildId)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        if (!member.GuildPermissions.Administrator)
            return Forbid();
        var result = await sender.Send(new PostDotaMetaCommand(guildId), HttpContext.RequestAborted);
        string dota = GameFeatureCatalog.Dota2.Key;
        return result.IsSuccess
            ? FeaturesDone(guildId, dota, "posting", text["Meta_Posted"].Value)
            : FeaturesDone(guildId, dota, "posting", text["That_didn_t_work"].Value, text.Message(result.Error.Description).Value, failed: true);
    }

    /// <summary>Short status lines for a game card: your sharing and the server's posts.</summary>
    private List<string> Status(GameFeatureInfo game, MyStats mine, DotaMetaSettings meta)
    {
        if (game.Placeholder)
            return [text["Features_StatusSoon"].Value];
        if (game == GameFeatureCatalog.Phasmophobia)
            return [text["Phasmo_Status", GhostDatabase.Ghosts.Count].Value];
        var lines = new List<string> { text[mine.Shares ? "Features_StatusSharing" : "Features_StatusNotSharing"].Value };
        if (game == GameFeatureCatalog.Dota2)
            lines.Add(meta.Schedule == MetaSchedule.Off || meta.ChannelId is null
                ? text["Features_StatusMetaOff"].Value
                : text["Features_StatusMeta", text[$"Meta_Schedule_{meta.Schedule}"].Value].Value);
        if (game == GameFeatureCatalog.Cs2 && !mine.Cs2Enabled)
            lines.Add(text["Features_StatusCs2Off"].Value);
        return lines;
    }

    private BannerModel? TakeFeaturesBanner() => TempData[FeaturesBanner] is string title
        ? new BannerModel(TempData[FeaturesBanner + "Kind"] as string ?? "success", title, TempData[FeaturesBanner + "Text"] as string)
        : null;

    private RedirectResult FeaturesDone(ulong guildId, string game, string anchor, string title, string? body = null, bool failed = false)
    {
        TempData[FeaturesBanner] = title;
        TempData[FeaturesBanner + "Text"] = body;
        TempData[FeaturesBanner + "Kind"] = failed ? "error" : "success";
        string key = GameFeatureCatalog.Find(game)?.Key ?? GameFeatureCatalog.Dota2.Key;
        return Redirect($"/game-features/{guildId}/{key}#{anchor}");
    }
}
