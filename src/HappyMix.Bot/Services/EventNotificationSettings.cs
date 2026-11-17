using System.Globalization;
using System.Text;
using Discord;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Events;
using MediatR;
using HappyMix.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// A change to a group's or event's channel notifications. Null values keep what is saved (or, for an event without
/// its own settings, what it inherits from its group), so commands can change one option at a time.
/// </summary>
public sealed record NotificationChange(bool? Enabled = null, ulong? ChannelId = null, bool Create = false, bool Inherit = false,
    string? Language = null, int? ReminderMinutes = null, bool ResetReminder = false, bool? Updates = null);

/// <summary>The settings in force for a group or event, and where they come from.</summary>
public sealed record EffectiveNotificationRoute(NotificationRoute Route, NotificationRouteSource Source, NotificationTarget Target);

public enum NotificationRouteSource { Own, Group, Default }

public sealed class EventNotificationSettings(NotificationRouteStore routes, ISender sender, NotificationChannels channels,
    IOptions<CommunityDefaults> community, IConfiguration configuration)
{
    public const int MaxReminderMinutes = 1440;

    /// <summary>The deployment's reminder lead time, used by routes without their own.</summary>
    public int DefaultReminderMinutes => Math.Clamp(configuration.GetValue("EventNotifications:ReminderMinutes", 15), 1, MaxReminderMinutes);

    public async Task<bool> CanManageAsync(IGuildUser member, string scope, Guid target) => (await sender.Send(
        new CanManageNotificationsQuery(member.GuildId, member.Id, member.GuildPermissions.Administrator, scope, target))).Value;

    /// <summary>The route that applies: the target's own, else (for an event) its group's, else switched off.</summary>
    public async Task<EffectiveNotificationRoute?> GetEffectiveAsync(ulong guildId, string scope, Guid target)
    {
        if ((await sender.Send(new GetNotificationTargetQuery(guildId, scope, target))).Value is not { } details)
            return null;
        if (await routes.GetAsync(guildId, scope, target) is { } own)
            return new(own, NotificationRouteSource.Own, details);
        if (details.GroupId is { } groupId && await routes.GetAsync(guildId, "group", groupId) is { } group)
            return new(group with { Scope = scope, TargetId = target.ToString() }, NotificationRouteSource.Group, details);
        return new(new NotificationRoute(guildId.ToString(CultureInfo.InvariantCulture), scope, target.ToString(), false, "0",
            community.Value.LanguageCode), NotificationRouteSource.Default, details);
    }

    /// <summary>Applies <paramref name="change"/> on top of the effective settings and saves the result.</summary>
    /// <exception cref="InvalidOperationException">The message is a resource key describing the problem.</exception>
    public async Task<NotificationRoute?> SaveAsync(IGuild guild, IGuildUser member, string scope, Guid target, NotificationChange change)
    {
        if (!await CanManageAsync(member, scope, target)) throw new InvalidOperationException("Notifications_Forbidden");
        if (change.Inherit && scope == "event") { await routes.ResetAsync(guild.Id, scope, target); return null; }
        if (change.Language is not null && CommunityDefaults.Normalize(change.Language) is null) throw new InvalidOperationException("Notifications_LanguageError");
        if (change.ReminderMinutes is < 1 or > MaxReminderMinutes) throw new InvalidOperationException("Notifications_LeadTimeError");
        var current = await GetEffectiveAsync(guild.Id, scope, target) ?? throw new InvalidOperationException("Notifications_Forbidden");

        ITextChannel? channel = change.ChannelId is > 0 ? await guild.GetTextChannelAsync(change.ChannelId.Value)
            : current.Route.Channel() is > 0 and var saved ? await guild.GetTextChannelAsync(saved) : null;
        if (change.ChannelId is > 0 && channel is null) throw new InvalidOperationException("Notifications_ChannelError");
        if (change.Create)
            channel = await channels.FindOrCreateAsync(guild, ChannelName(scope, current.Target.Title, target),
                NotificationText.Get(CommunityDefaults.Normalize(change.Language) ?? current.Route.Language, "Notifications_ChannelTopic", current.Target.Title));
        bool enabled = change.Enabled ?? (current.Route.Enabled || change.Create || change.ChannelId is > 0);
        if (enabled && channel is null) throw new InvalidOperationException("Notifications_ChooseChannel");
        if (channel is not null)
        {
            var bot = await guild.GetCurrentUserAsync();
            var permissions = bot.GetPermissions(channel);
            if (!member.GetPermissions(channel).ViewChannel || !permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks)
                throw new InvalidOperationException("Notifications_ChannelError");
        }

        var route = current.Route with
        {
            Enabled = enabled,
            ChannelId = (channel?.Id ?? 0).ToString(CultureInfo.InvariantCulture),
            Language = CommunityDefaults.Normalize(change.Language) ?? current.Route.Language,
            ReminderMinutes = change.ResetReminder ? null : change.ReminderMinutes ?? current.Route.ReminderMinutes,
            Updates = change.Updates ?? current.Route.Updates
        };
        await routes.SaveAsync(route);
        return route;
    }

    /// <summary>"group-thursday-scrims", from the title; the id when the title has nothing usable.</summary>
    internal static string ChannelName(string scope, string title, Guid target)
    {
        var slug = new StringBuilder();
        foreach (char character in title.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) slug.Append(character);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
            if (slug.Length >= 80) break;
        }
        string name = slug.ToString().Trim('-');
        return $"{scope}-{(name.Length == 0 ? target.ToString("N")[..8] : name)}";
    }
}
