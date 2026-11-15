using Discord;
using Discord.Net;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

public sealed record GameNewsPage(ulong GuildId, string GuildName, bool IsAdmin, string MemberId,
    IReadOnlyCollection<ulong> Roles, GameNewsSubscription[] Subscriptions, string? Error);

public sealed partial class DashboardController
{
    [HttpGet("/game-news/{guildId}")]
    public async Task<IActionResult> GameNews(ulong guildId, [FromServices] GameNewsService news)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        RememberServer(guildId);
        return View(new GameNewsPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator,
            member.Id.ToString(Culture), member.RoleIds, await news.ListAsync(guildId, HttpContext.RequestAborted),
            TempData["GameNewsError"] as string));
    }

    [HttpPost("/game-news/{guildId}")]
    public async Task<IActionResult> ChangeGameNews(ulong guildId, string action, string game, string? name,
        [FromServices] GameNewsService news)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member || discord.GetGuild(guildId) is not { } guild)
            return NotMember();
        if (action is not ("create" or "subscribe" or "unsubscribe" or "delete"))
            return BadRequest();

        try
        {
            await news.ChangeAsync(guild, member, GameNewsService.ParseAppId(game), action, name);
        }
        catch (InvalidOperationException error)
        {
            TempData["GameNewsError"] = error.Message;
        }
        catch (HttpException)
        {
            TempData["GameNewsError"] = "Discord could not finish the request. Check the bot’s permissions and role position, then try again.";
        }
        return RedirectToAction(nameof(GameNews), new { guildId });
    }
}
