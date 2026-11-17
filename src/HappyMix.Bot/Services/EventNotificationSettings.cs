using System.Globalization;
using Discord;
using HappyMix.Application.Events;
using MediatR;
using HappyMix.Infrastructure.Persistence;

namespace HappyMix.Bot.Services;

public sealed class EventNotificationSettings(NotificationRouteStore routes, ISender sender, NotificationChannels channels)
{
    public async Task<bool> CanManageAsync(IGuildUser member, string scope, Guid target) => (await sender.Send(
        new CanManageNotificationsQuery(member.GuildId, member.Id, member.GuildPermissions.Administrator, scope, target))).Value;

    public async Task SaveAsync(IGuild guild, IGuildUser member, string scope, Guid target, bool enabled,
        ulong? channelId, bool create, bool inherit, string language)
    {
        if (!await CanManageAsync(member, scope, target)) throw new InvalidOperationException("Notifications_Forbidden");
        if (inherit && scope == "event") { await routes.ResetAsync(guild.Id, scope, target); return; }
        if (language is not ("en" or "uk" or "ru")) throw new InvalidOperationException("Notifications_Language");
        ITextChannel? channel = channelId is > 0 ? await guild.GetTextChannelAsync(channelId.Value) : null;
        if (create) channel = await channels.FindOrCreateAsync(guild, $"{scope}-{target.ToString("N")[..8]}");
        if (enabled && channel is null) throw new InvalidOperationException("Notifications_ChooseChannel");
        if (channel is not null)
        {
            var bot = await guild.GetCurrentUserAsync();
            var permissions = bot.GetPermissions(channel);
            if (!member.GetPermissions(channel).ViewChannel || !permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks)
                throw new InvalidOperationException("Notifications_ChannelError");
        }
        await routes.SaveAsync(new(guild.Id.ToString(CultureInfo.InvariantCulture), scope, target.ToString(), enabled,
            (channel?.Id ?? 0).ToString(CultureInfo.InvariantCulture), language));
    }
}
