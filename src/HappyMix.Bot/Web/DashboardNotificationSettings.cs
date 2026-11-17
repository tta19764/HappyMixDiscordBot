using Discord;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

public sealed record EventNotificationForm(ulong GuildId, string Scope, Guid Target, NotificationRoute? Route,
    IReadOnlyList<ITextChannel> Channels, string? Error = null);

public sealed partial class DashboardController
{
    [HttpGet("/notifications/{guildId}/{scope}/{target:guid}")]
    public async Task<IActionResult> NotificationSettings(ulong guildId, string scope, Guid target,
        [FromServices] EventNotificationSettings settings, [FromServices] NotificationRouteStore routes)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member || !await settings.CanManageAsync(member, scope, target))
            return NotMember();
        var guild = discord.GetGuild(guildId);
        var channels = (await ((IGuild)guild).GetTextChannelsAsync()).Where(channel => member.GetPermissions(channel).ViewChannel).ToArray();
        return View("NotificationSettings", new EventNotificationForm(guildId, scope, target, await routes.GetAsync(guildId, scope, target), channels,
            TempData["NotificationError"] as string));
    }

    [HttpPost("/notifications/{guildId}/{scope}/{target:guid}")]
    public async Task<IActionResult> SaveNotificationSettings(ulong guildId, string scope, Guid target, bool enabled,
        ulong? channelId, bool create, bool inherit, string language, [FromServices] EventNotificationSettings settings)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member) return NotMember();
        try { await settings.SaveAsync(discord.GetGuild(guildId), member, scope, target, enabled, channelId, create, inherit, language); }
        catch (InvalidOperationException error) { TempData["NotificationError"] = text[error.Message].Value; }
        catch (Discord.Net.HttpException) { TempData["NotificationError"] = text["Notifications_ChannelError"].Value; }
        return RedirectToAction(nameof(NotificationSettings), new { guildId, scope, target });
    }
}
