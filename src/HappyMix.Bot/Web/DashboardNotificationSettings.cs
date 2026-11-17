using Discord;
using HappyMix.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>The channel notifications page of a group or event.</summary>
public sealed record EventNotificationForm(ulong GuildId, string Scope, Guid Target, EffectiveNotificationRoute Effective,
    IReadOnlyList<ITextChannel> Channels, int DefaultReminderMinutes, string? Error = null, bool Saved = false)
{
    public bool Inherits => Effective.Source != NotificationRouteSource.Own;
    public string BackUrl => $"/{(Scope == "group" ? "groups" : "events")}/{GuildId}/{Target}";
    public string SuggestedChannel => EventNotificationSettings.ChannelName(Scope, Effective.Target.Title, Target);
}

public sealed partial class DashboardController
{
    [HttpGet("/notifications/{guildId}/{scope}/{target:guid}")]
    public async Task<IActionResult> NotificationSettings(ulong guildId, string scope, Guid target,
        [FromServices] EventNotificationSettings settings)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member || !await settings.CanManageAsync(member, scope, target) ||
            await settings.GetEffectiveAsync(guildId, scope, target) is not { } effective)
            return NotMember();
        RememberServer(guildId);
        ViewData["NavGuildName"] = discord.GetGuild(guildId)?.Name;
        var guild = discord.GetGuild(guildId);
        var channels = guild.TextChannels
            .Where(channel => channel.GetChannelType() == ChannelType.Text && member.GetPermissions(channel).ViewChannel &&
                guild.CurrentUser.GetPermissions(channel) is { ViewChannel: true, SendMessages: true, EmbedLinks: true })
            .OrderBy(channel => channel.Position).Cast<ITextChannel>().ToArray();
        return View("NotificationSettings", new EventNotificationForm(guildId, scope, target, effective, channels,
            settings.DefaultReminderMinutes, TempData["NotificationError"] as string, TempData["NotificationSaved"] is true));
    }

    [HttpPost("/notifications/{guildId}/{scope}/{target:guid}")]
    public async Task<IActionResult> SaveNotificationSettings(ulong guildId, string scope, Guid target, bool enabled,
        ulong? channelId, bool create, bool inherit, string? language, int? reminderMinutes, bool updates,
        [FromServices] EventNotificationSettings settings)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member) return NotMember();
        try
        {
            // The page always shows every option, so each value is sent; a blank lead time means the server default.
            var change = new NotificationChange(enabled, channelId is > 0 ? channelId : null, create, inherit, language,
                reminderMinutes, reminderMinutes is null, updates);
            await settings.SaveAsync(discord.GetGuild(guildId), member, scope, target, change);
            TempData["NotificationSaved"] = true;
        }
        catch (InvalidOperationException error) { TempData["NotificationError"] = text[error.Message].Value; }
        catch (Discord.Net.HttpException) { TempData["NotificationError"] = text["Notifications_ChannelError"].Value; }
        return RedirectToAction(nameof(NotificationSettings), new { guildId, scope, target });
    }
}
