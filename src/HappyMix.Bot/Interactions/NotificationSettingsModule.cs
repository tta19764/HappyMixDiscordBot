using Discord;
using Discord.Interactions;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Persistence;

namespace HappyMix.Bot.Interactions;

[Group("notify", "Configure group or event channel reminders.")]
[RequireContext(ContextType.Guild)]
public sealed class NotificationSettingsModule(EventNotificationSettings settings, NotificationRouteStore routes)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("set", "Enable reminders, choose/create a channel, or inherit group settings.")]
    public async Task SetAsync([Choice("Group", "group"), Choice("Event", "event")] string scope, string id,
        bool enabled, [ChannelTypes(ChannelType.Text)] ITextChannel? channel = null, bool create = false, bool inherit = false,
        [Choice("English", "en"), Choice("Українська", "uk"), Choice("Русский", "ru")] string language = "en")
    {
        await DeferAsync(ephemeral: true);
        if (!Guid.TryParse(id, out var target)) { await FollowupAsync("Copy the group or event ID from its list command.", ephemeral: true); return; }
        try
        {
            await settings.SaveAsync(Context.Guild, (IGuildUser)Context.User, scope, target, enabled, channel?.Id, create, inherit, language);
            await FollowupAsync("Notification settings saved.", ephemeral: true);
        }
        catch (InvalidOperationException error) { await FollowupAsync(NotificationText.Get(language, error.Message), ephemeral: true); }
        catch (Discord.Net.HttpException) { await FollowupAsync(NotificationText.Get(language, "Notifications_ChannelError"), ephemeral: true); }
    }

    [SlashCommand("show", "Show the notification route for a group or event.")]
    public async Task ShowAsync([Choice("Group", "group"), Choice("Event", "event")] string scope, string id)
    {
        await DeferAsync(ephemeral: true);
        if (!Guid.TryParse(id, out var target) || !await settings.CanManageAsync((IGuildUser)Context.User, scope, target))
        { await FollowupAsync("Only the organizer or an administrator can manage these settings.", ephemeral: true); return; }
        var route = await routes.GetAsync(Context.Guild.Id, scope, target);
        await FollowupAsync(route is null ? "No override: events inherit their group's settings; groups default to disabled."
            : $"Enabled: {route.Enabled} · Channel: <#{route.ChannelId}> · Language: {route.Language}", ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}
