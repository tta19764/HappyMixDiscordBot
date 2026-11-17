using Discord;
using Discord.Interactions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using MediatR;
using HappyMix.Bot.Configuration;
using HappyMix.Bot.Services;
using HappyMix.Domain.Esports;
using HappyMix.Infrastructure.PandaScore;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Interactions;

[Group("settings", "Manage settings for this server.")]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.Administrator)]
[DefaultMemberPermissions(GuildPermission.Administrator)]
public sealed class GuildSettingsModule(ISender sender, IOptions<PandaScoreOptions> pandaScore,
    IOptions<DiscordOptions> discord, NotificationChannels channels)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("create-channel", "Create or reuse an announcement channel under Bot Notifications.")]
    public async Task CreateChannelAsync(Esport game)
    {
        await DeferAsync(ephemeral: true);
        var channel = await channels.FindOrCreateAsync(Context.Guild, game == Esport.CounterStrike2 ? "cs2-announcements" : "dota2-announcements");
        await sender.Send(new UpdateGuildSettingsCommand(Context.Guild.Id, pandaScore.Value.TimeZoneId, settings => settings with
        {
            Preferences = game == Esport.CounterStrike2
                ? (settings.Preferences ?? GuildEsportsPreferences.Default) with { CounterStrikeChannelId = channel.Id }
                : (settings.Preferences ?? GuildEsportsPreferences.Default) with { Dota2ChannelId = channel.Id }
        }));
        await FollowupAsync($"Announcements will use <#{channel.Id}>.", ephemeral: true);
    }

    [SlashCommand("broadcasts", "Configure automatic match stream discovery and notification language.")]
    public async Task BroadcastsAsync(Esport game, bool enabled,
        [Choice("English", "en"), Choice("Українська", "uk"), Choice("Русский", "ru")] string language = "en",
        string languages = "", string countries = "", string tags = "", string priority = "language,country,tags,viewers")
    {
        var discovery = new StreamDiscoveryPreferences(enabled, languages, countries, tags, priority);
        if (!discovery.IsValid || language is not ("en" or "uk" or "ru"))
        {
            await RespondAsync("Use each criterion at most once: language,country,tags,viewers. List them in priority order; omit unused criteria.", ephemeral: true);
            return;
        }
        await SavePreferencesAsync(preferences => game == Esport.CounterStrike2
            ? preferences with { CounterStrikeLanguage = language, CounterStrikeDiscovery = discovery }
            : preferences with { Dota2Language = language, Dota2Discovery = discovery }, "Broadcast settings saved.");
    }

    [SlashCommand("show", "Show saved server settings.")]
    public async Task ShowAsync()
    {
        var settings = (await sender.Send(new GetGuildSettingsQuery(Context.Guild.Id))).Value;
        var preferences = settings?.Preferences ?? GuildEsportsPreferences.Default;
        string channel = settings is { ChannelId: > 0 } ? $"<#{settings.ChannelId}>" : "Deployment default";
        string organizers = preferences.OrganizerRoleIds is { Length: > 0 } roles
            ? string.Join(", ", roles.Take(20).Select(role => $"<@&{role}>")) +
                (roles.Length > 20 ? $" (+{roles.Length - 20} more)" : "") : "None";
        await RespondAsync($"""
            Default esports channel: {channel}
            CS2 channel: {FormatChannel(preferences.CounterStrikeChannelId)}
            Dota 2 channel: {FormatChannel(preferences.Dota2ChannelId)}
            Time zone: {settings?.TimeZoneId ?? pandaScore.Value.TimeZoneId}
            CS2 teams: {FormatTeams(preferences.CounterStrikeTeamIds, GetCatalog(preferences, Esport.CounterStrike2))}
            Dota 2 teams: {FormatTeams(preferences.Dota2TeamIds, GetCatalog(preferences, Esport.Dota2))}
            Notifications: {preferences.Notifications}
            Match starts: {preferences.MatchStarts}
            Reminder: {preferences.ReminderMinutes} minutes before start
            Organizer roles: {organizers}
            CS2 reminder ping: {FormatRole(preferences.CounterStrikePingRoleId)}
            Dota 2 reminder ping: {FormatRole(preferences.Dota2PingRoleId)}
            CS2 Twitch: {FormatTwitchChannels(preferences.CounterStrikeTwitchChannels ?? discord.Value.CounterStrikeTwitchChannels)}
            Dota 2 Twitch: {FormatTwitchChannels(preferences.Dota2TwitchChannels ?? discord.Value.Dota2TwitchChannels)}
            """, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("set", "Save the server channel and time zone together.")]
    public Task SetAsync(
        [Summary(description: "Text channel for esports announcements"), ChannelTypes(ChannelType.Text)] ITextChannel channel,
        [Summary(description: "IANA time zone, for example Europe/Kyiv or UTC")] string timezone) =>
        SaveLocationAsync(channel, timezone);

    [SlashCommand("channel", "Set the default channel or a channel for one game.")]
    public async Task ChannelAsync(
        [Summary(description: "Text channel. Omit it with a game to restore the default."), ChannelTypes(ChannelType.Text)] ITextChannel? channel = null,
        [Summary(description: "Game to configure. Omit it to change the default channel.")] Esport? game = null)
    {
        if (channel is null && game is null)
        {
            await RespondAsync("Choose a channel to set the default, or choose a game to clear its override.", ephemeral: true);
            return;
        }

        await SaveLocationAsync(channel, null, game);
    }

    [SlashCommand("timezone", "Set the time zone used for the daily schedule.")]
    public Task TimeZoneAsync(
        [Summary(description: "IANA time zone, for example Europe/Kyiv or UTC")] string timezone) =>
        SaveLocationAsync(null, timezone);

    [SlashCommand("add-team", "Add or update a team name in this server's catalog.")]
    public async Task AddTeamAsync(Esport game, string name,
        [Summary(description: "PandaScore team ID for this game"), MinValue(1)] long teamId)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40 || name.Contains(',') || name.Any(char.IsControl) ||
            string.Equals(name, "default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "none", StringComparison.OrdinalIgnoreCase) || teamId <= 0)
        {
            await RespondAsync("Use a name of 1 to 40 characters without commas, and a positive PandaScore team ID. Default and none are reserved.",
                ephemeral: true);
            return;
        }

        await SavePreferencesAsync(preferences =>
        {
            var catalog = GetCatalog(preferences, game);
            catalog[name] = teamId;
            return game == Esport.CounterStrike2
                ? preferences with { CounterStrikeTeamAliases = catalog }
                : preferences with { Dota2TeamAliases = catalog };
        }, $"Saved {name} ({teamId}) in this server's {game.DisplayName()} catalog.");
    }

    [SlashCommand("team-catalog", "List team names and PandaScore IDs for one game.")]
    public async Task TeamCatalogAsync(Esport game, [MinValue(1)] int page = 1)
    {
        const int pageSize = 20;
        var settings = (await sender.Send(new GetGuildSettingsQuery(Context.Guild.Id))).Value;
        var catalog = GetCatalog(settings?.Preferences ?? GuildEsportsPreferences.Default, game);
        int pages = Math.Max(1, (catalog.Count + pageSize - 1) / pageSize);
        if (page < 1 || page > pages)
        {
            await RespondAsync($"Choose a page from 1 to {pages}.", ephemeral: true);
            return;
        }

        string teams = string.Join("\n", catalog.OrderBy(team => team.Key, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(team => $"{team.Key} вЂ” {team.Value}"));
        await RespondAsync(teams.Length == 0 ? "This catalog is empty." : $"{game.DisplayName()} catalog ({page}/{pages})\n{teams}",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("teams", "Choose followed teams for one game.")]
    public async Task TeamsAsync(Esport game,
        [Summary(description: "Comma-separated team names or PandaScore IDs, default, or none")] string teams)
    {
        var settings = (await sender.Send(new GetGuildSettingsQuery(Context.Guild.Id))).Value;
        var catalog = GetCatalog(settings?.Preferences ?? GuildEsportsPreferences.Default, game);
        long[]? ids = null;
        teams = teams.Trim();
        if (string.Equals(teams, "none", StringComparison.OrdinalIgnoreCase))
        {
            ids = [];
        }
        else if (!string.Equals(teams, "default", StringComparison.OrdinalIgnoreCase))
        {
            var parts = teams.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > discord.Value.MaxFollowedTeamsPerGame)
            {
                await RespondAsync($"Enter 1 to {discord.Value.MaxFollowedTeamsPerGame} teams, default, or none.", ephemeral: true);
                return;
            }

            var selected = new List<long>();
            foreach (string part in parts)
            {
                if (!catalog.TryGetValue(part, out long id) &&
                    (!long.TryParse(part, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out id) || id <= 0))
                {
                    string guidance = catalog.Count == 0 ? "Enter a PandaScore team ID."
                        : "Use a PandaScore team ID or a name from /settings team-catalog.";
                    await RespondAsync(guidance,
                        ephemeral: true);
                    return;
                }
                selected.Add(id);
            }
            ids = selected.Distinct().ToArray();
        }

        await SavePreferencesAsync(preferences => game == Esport.CounterStrike2
            ? preferences with { CounterStrikeTeamIds = ids } : preferences with { Dota2TeamIds = ids },
            $"Saved {game.DisplayName()} teams: {FormatTeams(ids, catalog)}.");
    }

    [SlashCommand("notifications", "Choose which esports messages the bot posts.")]
    public Task NotificationsAsync(bool schedule, bool reminders, bool maps, bool results, bool matchStarts = true)
    {
        var enabled = EsportsNotifications.None;
        if (schedule) enabled |= EsportsNotifications.Schedule;
        if (reminders) enabled |= EsportsNotifications.Reminders;
        if (maps) enabled |= EsportsNotifications.Maps;
        if (results) enabled |= EsportsNotifications.Results;
        return SavePreferencesAsync(preferences => preferences with { Notifications = enabled, MatchStarts = matchStarts },
            $"Enabled esports notifications: {enabled}. Match starts: {matchStarts}.");
    }

    [SlashCommand("reminder", "Set how many minutes before a match to send a reminder.")]
    public Task ReminderAsync([MinValue(1), MaxValue(1440)] int minutes) =>
        SavePreferencesAsync(preferences => preferences with { ReminderMinutes = minutes },
            $"Saved reminder lead time of {minutes} minutes.");

    [SlashCommand("organizer-role", "Add, remove, or clear roles reserved for group organizers.")]
    public async Task OrganizerRoleAsync(OrganizerRoleAction action, IRole? role = null)
    {
        if (action != OrganizerRoleAction.Clear && (role is null || role.Guild.Id != Context.Guild.Id || role.Id == Context.Guild.Id))
        {
            await RespondAsync("Choose a role from this server other than @everyone.", ephemeral: true);
            return;
        }

        await SavePreferencesAsync(preferences =>
        {
            ulong[] existing = preferences.OrganizerRoleIds ?? [];
            ulong[] updated = action switch
            {
                OrganizerRoleAction.Add => existing.Append(role!.Id).Distinct().ToArray(),
                OrganizerRoleAction.Remove => existing.Where(id => id != role!.Id).ToArray(),
                _ => []
            };
            return preferences with { OrganizerRoleIds = updated };
        }, "Organizer roles saved. View them with /settings show.");
    }

    [SlashCommand("ping-role", "Set a role to mention in match reminders. Omit the role to clear it.")]
    public async Task PingRoleAsync(Esport game, IRole? role = null)
    {
        if (role is not null && (role.Guild.Id != Context.Guild.Id || role.Id == Context.Guild.Id))
        {
            await RespondAsync("Choose a role from this server other than @everyone.", ephemeral: true);
            return;
        }

        await SavePreferencesAsync(preferences => game == Esport.CounterStrike2
            ? preferences with { CounterStrikePingRoleId = role?.Id } : preferences with { Dota2PingRoleId = role?.Id },
            $"Saved {game.DisplayName()} reminder ping: {FormatRole(role?.Id)}.");
    }

    [SlashCommand("twitch", "Set preferred Twitch channels for one game.")]
    public async Task TwitchAsync(Esport game,
        [Summary(description: "Comma-separated Twitch handles or links, default, or none")] string channels)
    {
        channels = channels.Trim();
        string[]? saved = null;
        if (string.Equals(channels, "none", StringComparison.OrdinalIgnoreCase))
        {
            saved = [];
        }
        else if (!string.Equals(channels, "default", StringComparison.OrdinalIgnoreCase))
        {
            var handles = channels.Split(',').Select(TwitchChannels.GetHandle).ToArray();
            if (handles.Length > discord.Value.MaxTwitchChannelsPerGame || handles.Any(handle => handle is null))
            {
                await RespondAsync($"Enter up to {discord.Value.MaxTwitchChannelsPerGame} Twitch channel names or HTTPS links separated by commas. Use default to reset or none to hide links.",
                    ephemeral: true);
                return;
            }
            saved = handles.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        }

        await SavePreferencesAsync(preferences => game == Esport.CounterStrike2
            ? preferences with { CounterStrikeTwitchChannels = saved } : preferences with { Dota2TwitchChannels = saved },
            $"Saved {game.DisplayName()} Twitch channels: {(saved is null ? "Defaults" : FormatTwitchChannels(saved))}.");
    }

    private static string FormatTwitchChannels(string[] channels) =>
        channels.Length == 0 ? "None" : string.Join(", ", channels.Take(5)) +
            (channels.Length > 5 ? $" (+{channels.Length - 5} more)" : "");

    [SlashCommand("reset-channel", "Restore the deployment default announcement channel.")]
    public Task ResetChannelAsync() =>
        SaveAsync(saved => saved with { ChannelId = 0 }, "Restored the deployment default channel.");

    [SlashCommand("remove-team", "Remove a team alias from this server's catalog.")]
    public Task RemoveTeamAsync(Esport game, string name) =>
        ChangeCatalogAsync(game, name.Trim());

    [SlashCommand("reset-catalog", "Restore the deployment team catalog for a game.")]
    public Task ResetCatalogAsync(Esport game) => ChangeCatalogAsync(game, null);

    private Task ChangeCatalogAsync(Esport game, string? name) =>
        SavePreferencesAsync(preferences =>
        {
            Dictionary<string, long>? catalog = null;
            if (name is not null)
            {
                catalog = GetCatalog(preferences, game);
                catalog.Remove(name);
            }
            return game == Esport.CounterStrike2
                ? preferences with { CounterStrikeTeamAliases = catalog }
                : preferences with { Dota2TeamAliases = catalog };
        }, "Team catalog updated.");

    private async Task SaveLocationAsync(ITextChannel? channel, string? timezone, Esport? game = null)
    {
        if (channel is not null && (channel.GuildId != Context.Guild.Id || channel.GetChannelType() != ChannelType.Text))
        {
            await RespondAsync("Choose a text channel in this server.", ephemeral: true);
            return;
        }

        if (channel is not null)
        {
            var permissions = Context.Guild.CurrentUser.GetPermissions(channel);
            if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks)
            {
                await RespondAsync("The bot needs View Channel, Send Messages, and Embed Links in that channel.",
                    ephemeral: true);
                return;
            }
        }

        TimeZoneInfo? zone = null;
        if (timezone is not null && (timezone.Length > 100 ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(timezone.Trim(), out zone) ||
            (!zone.HasIanaId && zone.Id != "UTC")))
        {
            await RespondAsync("Enter an IANA time zone, such as Europe/Kyiv or UTC.", ephemeral: true);
            return;
        }

        if (game is { } discipline)
        {
            await SavePreferencesAsync(preferences => discipline == Esport.CounterStrike2
                ? preferences with { CounterStrikeChannelId = channel?.Id }
                : preferences with { Dota2ChannelId = channel?.Id },
                $"Saved {discipline.DisplayName()} channel: {FormatChannel(channel?.Id)}.");
            return;
        }

        await SaveAsync(settings => settings with
        {
            ChannelId = channel?.Id ?? settings.ChannelId,
            TimeZoneId = zone?.Id ?? settings.TimeZoneId
        }, "Saved. Use /settings show to review the current settings.");
    }

    private Task SavePreferencesAsync(Func<GuildEsportsPreferences, GuildEsportsPreferences> update, string response) =>
        SaveAsync(settings => settings with { Preferences = update(settings.Preferences ?? GuildEsportsPreferences.Default) },
            response);

    private async Task SaveAsync(Func<GuildSettings, GuildSettings> update, string response)
    {
        await DeferAsync(ephemeral: true);
        await sender.Send(new UpdateGuildSettingsCommand(Context.Guild.Id, pandaScore.Value.TimeZoneId, update));
        await ModifyOriginalResponseAsync(message =>
        {
            message.Content = response;
            message.AllowedMentions = AllowedMentions.None;
        });
    }

    private static string FormatRole(ulong? roleId) => roleId is { } id ? $"<@&{id}>" : "None";
    private static string FormatChannel(ulong? channelId) => channelId is { } id ? $"<#{id}>" : "Uses default";

    private Dictionary<string, long> GetCatalog(GuildEsportsPreferences preferences, Esport game)
    {
        var aliases = game == Esport.CounterStrike2
            ? preferences.CounterStrikeTeamAliases ?? pandaScore.Value.CounterStrikeTeamAliases
            : preferences.Dota2TeamAliases ?? pandaScore.Value.Dota2TeamAliases;
        return new Dictionary<string, long>(aliases, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatTeams(long[]? ids, Dictionary<string, long> catalog) => ids switch
    {
        null => "Default team list",
        [] => "None",
        _ => string.Join(", ", ids.Select(id => catalog.FirstOrDefault(pair => pair.Value == id).Key
            ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
    };
}

public enum OrganizerRoleAction
{
    Add,
    Remove,
    Clear
}
