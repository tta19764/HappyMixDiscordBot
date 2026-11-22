using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Stats;
using HappyMix.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>
/// The Game features page: the viewer's own Dota 2 / CS2 stats (the same data and switch as group Stats tabs) and
/// per-game server features, starting with the Dota 2 lane meta. Admins set where and how often it's posted.
/// </summary>
public sealed record GameFeaturesPage(ulong GuildId, string GuildName, bool IsAdmin, GameFeatures Data, IReadOnlyList<(ulong Id, string Name)> Channels,
    string TimeZone, BannerModel? Banner);

/// <summary>The viewer's own stat cards with the sharing switch and Refresh, as on the Game features page.</summary>
public sealed record OwnStatsModel(ulong GuildId, MyStats Mine, string ShareUrl, string RefreshUrl, DateTimeOffset Now);

public sealed partial class DashboardController
{
    private const string FeaturesBanner = "FeaturesBanner";

    [HttpGet("/game-features/{guildId}")]
    public async Task<IActionResult> GameFeatures(ulong guildId, MetaPatch? patch, int? role, MetaRank? rank, MetaSort? sort)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        RememberServer(guildId);
        // The search bar posts a GET; without it the page starts from the server's saved post filters.
        MetaFilter? filter = patch is null && role is null && rank is null && sort is null ? null
            : new MetaFilter(patch ?? MetaPatch.Current, role is >= 0 and <= 5 ? role.Value : 0, rank ?? MetaRank.All, sort ?? MetaSort.Score);
        var data = (await sender.Send(new GetGameFeaturesQuery(guildId, member.Id, filter), HttpContext.RequestAborted)).Value;
        var channels = discord.GetGuild(guildId)?.TextChannels.OrderBy(channel => channel.Position).Select(channel => (channel.Id, channel.Name)).ToArray()
            ?? [];
        BannerModel? banner = TempData[FeaturesBanner] is string title
            ? new BannerModel(TempData[FeaturesBanner + "Kind"] as string ?? "success", title, TempData[FeaturesBanner + "Text"] as string)
            : null;
        return View("GameFeatures", new GameFeaturesPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator, data, channels,
            (await GetGuildZoneAsync(guildId, HttpContext.RequestAborted)).Id, banner));
    }

    [HttpPost("/game-features/{guildId}/stats/share")]
    public async Task<IActionResult> ShareStatsFromFeatures(ulong guildId, bool share)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        await sender.Send(new SetStatsSharingCommand(member.Id, share, guildId), HttpContext.RequestAborted);
        return FeaturesDone(guildId, "stats", text[share ? "Stats_Shared" : "Stats_Unshared"].Value);
    }

    [HttpPost("/game-features/{guildId}/stats/refresh")]
    public async Task<IActionResult> RefreshStatsFromFeatures(ulong guildId)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        await sender.Send(new RefreshMyStatsCommand(member.Id, guildId), HttpContext.RequestAborted);
        return FeaturesDone(guildId, "stats", text["Stats_Refreshed"].Value);
    }

    [HttpPost("/game-features/{guildId}/dota-meta")]
    public async Task<IActionResult> SaveDotaMeta(ulong guildId, MetaSchedule schedule, ulong? channel, DayOfWeek day, int hour, int count = DotaMetas.DefaultCount,
        MetaRank postRank = MetaRank.All, MetaPatch postPatch = MetaPatch.Current, MetaSort postSort = MetaSort.Score)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        if (!member.GuildPermissions.Administrator)
            return Forbid();
        var result = await sender.Send(new SaveDotaMetaCommand(guildId, schedule, channel is > 0 ? channel : null, day, hour, count, postRank, postPatch, postSort), HttpContext.RequestAborted);
        return result.IsSuccess
            ? FeaturesDone(guildId, "dota-meta", text["Meta_Saved"].Value)
            : FeaturesDone(guildId, "dota-meta", text["That_didn_t_work"].Value, text.Message(result.Error.Description).Value, failed: true);
    }

    /// <summary>Creates a #dota-meta channel under "Bot Notifications" and makes it the meta channel.</summary>
    [HttpPost("/game-features/{guildId}/dota-meta/channel")]
    public async Task<IActionResult> CreateDotaMetaChannel(ulong guildId, [FromServices] NotificationChannels channels)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        if (!member.GuildPermissions.Administrator)
            return Forbid();
        if (discord.GetGuild(guildId) is not { } guild)
            return FeaturesDone(guildId, "dota-meta", text["That_didn_t_work"].Value, text["Meta_NoGuild"].Value, failed: true);
        try
        {
            var created = await channels.CreateAsync(guild, "dota-meta", text["Meta_ChannelTopic"].Value);
            var saved = (await sender.Send(new GetGameFeaturesQuery(guildId, member.Id), HttpContext.RequestAborted)).Value.Meta;
            await sender.Send(new SaveDotaMetaCommand(guildId, saved.Schedule, created.Id, saved.Day, saved.Hour, saved.Count, saved.Rank, saved.Patch,
                saved.Sort), HttpContext.RequestAborted);
            return FeaturesDone(guildId, "dota-meta", text["Meta_ChannelCreated", "#" + created.Name].Value);
        }
        catch (Discord.Net.HttpException error)
        {
            return FeaturesDone(guildId, "dota-meta", text["That_didn_t_work"].Value,
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
        return result.IsSuccess
            ? FeaturesDone(guildId, "dota-meta", text["Meta_Posted"].Value)
            : FeaturesDone(guildId, "dota-meta", text["That_didn_t_work"].Value, text.Message(result.Error.Description).Value, failed: true);
    }

    private RedirectResult FeaturesDone(ulong guildId, string anchor, string title, string? body = null, bool failed = false)
    {
        TempData[FeaturesBanner] = title;
        TempData[FeaturesBanner + "Text"] = body;
        TempData[FeaturesBanner + "Kind"] = failed ? "error" : "success";
        return Redirect($"/game-features/{guildId}#{anchor}");
    }
}
