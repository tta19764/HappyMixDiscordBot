using Discord;
using Discord.Interactions;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Events;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Persistence;
using MediatR;

namespace HappyMix.Bot.Interactions;

[Group("notify", "Configure group or event channel notifications.")]
[RequireContext(ContextType.Guild)]
public sealed class NotificationSettingsModule(EventNotificationSettings settings)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("set", "Change channel notifications. Omitted options keep their current values.")]
    public async Task SetAsync(
        [Summary(description: "Group or event to configure"), Autocomplete(typeof(NotificationTargetAutocomplete))] string target,
        [Summary(description: "Turn channel notifications on or off")] bool? enabled = null,
        [Summary(description: "Channel for reminders and updates"), ChannelTypes(ChannelType.Text)] ITextChannel? channel = null,
        [Summary(description: "Create a channel named after the group or event in Bot Notifications")] bool create = false,
        [Summary(description: "Language of the posts"), Choice("English", "en"), Choice("Українська", "uk"), Choice("Русский", "ru")] string? language = null,
        [Summary(description: "Minutes before the start to remind; 0 uses the server default"), MinValue(0), MaxValue(1440)] int? reminder = null,
        [Summary(description: "Also post when events are scheduled, confirmed or canceled")] bool? updates = null,
        [Summary(description: "Events only: drop own settings and use the group's again")] bool inherit = false)
    {
        await DeferAsync(ephemeral: true);
        if (!TryParse(target, out string scope, out Guid id))
        {
            await FollowupAsync("Pick a group or event from the list.", ephemeral: true);
            return;
        }
        string reply = language ?? "en";
        try
        {
            var change = new NotificationChange(enabled, channel?.Id, create, inherit, language,
                reminder is > 0 ? reminder : null, reminder == 0, updates);
            await settings.SaveAsync(Context.Guild, (IGuildUser)Context.User, scope, id, change);
            var effective = await settings.GetEffectiveAsync(Context.Guild.Id, scope, id);
            reply = effective?.Route.Language ?? reply;
            await FollowupAsync(NotificationText.Get(reply, "Notifications_Saved") + "\n" + Describe(effective), ephemeral: true,
                allowedMentions: AllowedMentions.None);
        }
        catch (InvalidOperationException error) { await FollowupAsync(NotificationText.Get(reply, error.Message), ephemeral: true); }
        catch (Discord.Net.HttpException) { await FollowupAsync(NotificationText.Get(reply, "Notifications_ChannelError"), ephemeral: true); }
    }

    [SlashCommand("show", "Show the channel notifications that apply to a group or event.")]
    public async Task ShowAsync(
        [Summary(description: "Group or event"), Autocomplete(typeof(NotificationTargetAutocomplete))] string target)
    {
        await DeferAsync(ephemeral: true);
        if (!TryParse(target, out string scope, out Guid id) || !await settings.CanManageAsync((IGuildUser)Context.User, scope, id))
        {
            await FollowupAsync("Only the organizer or an administrator can manage these settings.", ephemeral: true);
            return;
        }
        await FollowupAsync(Describe(await settings.GetEffectiveAsync(Context.Guild.Id, scope, id)), ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    /// <summary>"group:&lt;id&gt;" or "event:&lt;id&gt;" from autocomplete; a bare id is tried as an event, then a group.</summary>
    internal static bool TryParse(string value, out string scope, out Guid id)
    {
        string[] parts = value.Split(':', 2);
        scope = parts.Length == 2 ? parts[0] : "";
        if (parts.Length == 2 && scope is "group" or "event")
            return Guid.TryParse(parts[1], out id);
        scope = "event";
        return Guid.TryParse(value, out id);
    }

    private string Describe(EffectiveNotificationRoute? effective)
    {
        if (effective is null)
            return "That group or event no longer exists.";
        var route = effective.Route;
        string language = route.Language;
        string source = NotificationText.Get(language, "Notifications_Source_" + effective.Source);
        string channel = route.Channel() is > 0 and var id ? $"<#{id}>" : "—";
        int minutes = route.ReminderMinutes ?? settings.DefaultReminderMinutes;
        return NotificationText.Get(language, "Notifications_Summary", Format.Sanitize(effective.Target.Title), source,
            NotificationText.Get(language, route.Enabled ? "Notifications_On" : "Notifications_Off"), channel,
            Web.DashboardController.LanguageName(language), minutes,
            NotificationText.Get(language, route.Updates ? "Notifications_On" : "Notifications_Off"));
    }
}

/// <summary>Groups and upcoming events the member may configure, newest names first, filtered by what they type.</summary>
public sealed class NotificationTargetAutocomplete(ISender sender, IClock clock) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.User is not IGuildUser member)
            return AutocompletionResult.FromSuccess();
        string typed = autocompleteInteraction.Data.Current.Value?.ToString()?.Trim() ?? "";
        var targets = (await sender.Send(new ListManageableNotificationTargetsQuery(member.GuildId, member.Id,
            member.GuildPermissions.Administrator, clock.UtcNow))).Value;
        var results = targets
            .Where(target => typed.Length == 0 || target.Title.Contains(typed, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(target => target.Scope == "group" ? 0 : 1).ThenBy(target => target.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(25)
            .Select(target => new AutocompleteResult(Label(target), $"{target.Scope}:{target.Id}"));
        return AutocompletionResult.FromSuccess(results);
    }

    private static string Label(NotificationTarget target)
    {
        string label = (target.Scope == "group" ? "Group · " : "Event · ") + target.Title;
        return label.Length <= 100 ? label : label[..99] + "…";
    }
}
