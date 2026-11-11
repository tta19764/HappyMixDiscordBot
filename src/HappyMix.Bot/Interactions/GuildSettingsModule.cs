using Discord;
using Discord.Interactions;
using HappyMix.Application.Abstractions.Guilds;

namespace HappyMix.Bot.Interactions;

[Group("settings", "Manage settings for this server.")]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.Administrator)]
public sealed class GuildSettingsModule(IGuildSettingsStore store) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("show", "Show saved server settings.")]
    public async Task ShowAsync()
    {
        var settings = await store.GetAsync(Context.Guild.Id);
        await RespondAsync(settings is null
            ? "No settings saved. Use /settings set to configure this server."
            : $"Channel: <#{settings.ChannelId}>\nTime zone: {settings.TimeZoneId}", ephemeral: true);
    }

    [SlashCommand("set", "Save the server channel and time zone.")]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    public async Task SetAsync(
        [Summary(description: "Text channel for server updates")] ITextChannel channel,
        [Summary(description: "IANA time zone, for example Europe/Kyiv or UTC")] string timezone)
    {
        if (channel.GuildId != Context.Guild.Id)
        {
            await RespondAsync("Choose a text channel in this server.", ephemeral: true);
            return;
        }

        if (timezone.Length > 100 || !TimeZoneInfo.TryFindSystemTimeZoneById(timezone.Trim(), out var zone)
            || (!zone.HasIanaId && zone.Id != "UTC"))
        {
            await RespondAsync("Enter an IANA time zone, such as Europe/Kyiv or UTC.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await store.SaveAsync(new GuildSettings(Context.Guild.Id, channel.Id, zone.Id));
        await ModifyOriginalResponseAsync(message =>
            message.Content = $"Saved channel <#{channel.Id}> and time zone {zone.Id}.");
    }
}
