using Discord;
using Discord.Net;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

public sealed record GameNewsPage(ulong GuildId, string GuildName, bool IsAdmin, string MemberId,
    IReadOnlyCollection<ulong> Roles, GameNewsSubscription[] Subscriptions, string? Error, Dictionary<string, string[]> Subscribers, bool MembersAvailable);

public sealed partial class DashboardController
{
    [HttpGet("/game-news/{guildId}")]
    public async Task<IActionResult> GameNews(ulong guildId, [FromServices] GameNewsService news)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        var subscribers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        bool membersAvailable = true;
        try
        {
            var guild = await discord.Rest.GetGuildAsync(guildId);
            await foreach (var page in guild.GetUsersAsync().WithCancellation(HttpContext.RequestAborted))
                foreach (var user in page)
                    foreach (ulong role in user.RoleIds)
                    {
                        string key = role.ToString(Culture);
                        if (!subscribers.TryGetValue(key, out var portraits))
                            subscribers[key] = portraits = [];
                        portraits.Add(user.GetDisplayAvatarUrl(size: 64));
                    }
        }
        catch (HttpException)
        {
            membersAvailable = false;
        }
        RememberServer(guildId);
        return View(new GameNewsPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator,
            member.Id.ToString(Culture), member.RoleIds, await news.ListAsync(guildId, HttpContext.RequestAborted),
            TempData["GameNewsError"] as string, subscribers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), membersAvailable));
    }

    [HttpPost("/game-news/{guildId}")]
    public async Task<IActionResult> ChangeGameNews(ulong guildId, string action, string game, string? name, int[]? sources,
        [FromServices] GameNewsService news)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member || discord.GetGuild(guildId) is not { } guild)
            return NotMember();
        if (action is not ("create" or "subscribe" or "unsubscribe" or "delete" or "sources"))
            return BadRequest();
        // Ticked checkboxes post their flag values; a form without them (subscribe, delete) keeps the default.
        var kinds = sources is null ? GameNewsSources.Official
            : (GameNewsSources)sources.Aggregate(0, (all, flag) => all | flag) & GameNewsSources.All;

        try
        {
            await news.ChangeAsync(guild, member, GameNewsService.ParseAppId(game), action, name, kinds);
        }
        catch (InvalidOperationException error)
        {
            TempData["GameNewsError"] = error.Message;
        }
        catch (HttpException)
        {
            TempData["GameNewsError"] = "Discord could not finish the request. Check the bot’s permissions and role position, then try again.";
        }
        return action == "sources"
            ? Redirect($"/game-news/{guildId}#news-{GameNewsService.ParseAppId(game)}")
            : RedirectToAction(nameof(GameNews), new { guildId });
    }
}
