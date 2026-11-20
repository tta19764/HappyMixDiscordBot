using Discord;
using Discord.Net;
using HappyMix.Bot.Services;
using HappyMix.Application.Abstractions.News;
using HappyMix.Application.News;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

public sealed record GameNewsPage(ulong GuildId, string GuildName, bool IsAdmin, string MemberId,
    IReadOnlyCollection<ulong> Roles, GameNewsSubscription[] Subscriptions, string? Error, Dictionary<string, string[]> Subscribers, bool MembersAvailable);

public sealed partial class DashboardController
{
    [HttpGet("/game-news/{guildId}")]
    public async Task<IActionResult> GameNews(ulong guildId)
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
            member.Id.ToString(Culture), member.RoleIds, (await sender.Send(new GetGameNewsQuery(guildId), HttpContext.RequestAborted)).Value,
            TempData["GameNewsError"] as string, subscribers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), membersAvailable));
    }

    [HttpPost("/game-news/{guildId}")]
    public async Task<IActionResult> ChangeGameNews(ulong guildId, string action, string game, string? name, int[]? sources)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        // Ticked checkboxes post their flag values; a form without them (subscribe, delete) keeps the default.
        var kinds = sources is null ? GameNewsSources.Official
            : (GameNewsSources)sources.Aggregate(0, (all, flag) => all | flag) & GameNewsSources.All;
        uint appId = GameNewsService.ParseAppId(game);
        bool admin = member.GuildPermissions.Administrator;
        HappyMix.Domain.Abstractions.Result<GameNewsSubscription>? result = action switch
        {
            "create" => await sender.Send(new CreateGameNewsCommand(guildId, member.Id, appId, name ?? "", kinds), HttpContext.RequestAborted),
            "subscribe" or "unsubscribe" => await sender.Send(new SubscribeGameNewsCommand(guildId, member.Id, appId, action == "subscribe"), HttpContext.RequestAborted),
            "delete" => await sender.Send(new DeleteGameNewsCommand(guildId, member.Id, admin, appId), HttpContext.RequestAborted),
            "sources" => await sender.Send(new SetGameNewsSourcesCommand(guildId, member.Id, admin, appId, kinds), HttpContext.RequestAborted),
            _ => null
        };
        if (result is null)
            return BadRequest();
        if (result.IsFailure)
            TempData["GameNewsError"] = result.Error.Description;
        return action == "sources"
            ? Redirect($"/game-news/{guildId}#news-{GameNewsService.ParseAppId(game)}")
            : RedirectToAction(nameof(GameNews), new { guildId });
    }
}
