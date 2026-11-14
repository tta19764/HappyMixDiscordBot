using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Bot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace HappyMix.Bot.Web;

public sealed class ServerSettingsForm
{
    public ulong ChannelId { get; set; }
    [Required(ErrorMessage = "Enter a time zone such as Europe/Kyiv or UTC."), StringLength(100)]
    public string TimeZone { get; set; } = "UTC";
    [Range(1, 1440, ErrorMessage = "Enter a lead time between 1 and 1440 minutes.")]
    public int ReminderMinutes { get; set; } = 30;
    public bool Schedule { get; set; }
    public bool Reminders { get; set; }
    public bool Maps { get; set; }
    public bool Results { get; set; }
    public bool MatchStarts { get; set; }
    public ulong[] OrganizerRoles { get; set; } = [];
    public GameSettingsForm Cs { get; set; } = new();
    public GameSettingsForm Dota { get; set; } = new();
}

/// <summary>Per-game overrides. Each list has a source: inherit the default, a custom value, or none.</summary>
public sealed class GameSettingsForm
{
    public const string Inherit = "default";
    public const string Custom = "custom";
    public const string None = "none";

    public ulong? Channel { get; set; }
    public ulong? PingRole { get; set; }
    public string TeamsMode { get; set; } = Inherit;
    [StringLength(500)] public string? Teams { get; set; }
    public string TwitchMode { get; set; } = Inherit;
    [StringLength(500)] public string? Twitch { get; set; }
    public string CatalogMode { get; set; } = Inherit;
    [StringLength(16000)] public string? Catalog { get; set; }
}

public sealed partial class DashboardController
{
    [HttpGet("/settings/{guildId}")]
    public async Task<IActionResult> Settings(ulong guildId)
    {
        if (await GetAdministratorAsync(guildId) is not { } guild)
            return await SettingsForbiddenAsync(guildId);

        RememberServer(guildId);
        var saved = (await sender.Send(new GetGuildSettingsQuery(guildId), HttpContext.RequestAborted)).Value;
        var prefs = saved?.Preferences ?? GuildEsportsPreferences.Default;
        var form = new ServerSettingsForm
        {
            ChannelId = saved?.ChannelId ?? 0,
            TimeZone = saved?.TimeZoneId ?? options.Value.TimeZoneId,
            ReminderMinutes = prefs.ReminderMinutes,
            Schedule = prefs.Notifications.HasFlag(EsportsNotifications.Schedule),
            Reminders = prefs.Notifications.HasFlag(EsportsNotifications.Reminders),
            Maps = prefs.Notifications.HasFlag(EsportsNotifications.Maps),
            Results = prefs.Notifications.HasFlag(EsportsNotifications.Results),
            MatchStarts = prefs.MatchStarts,
            OrganizerRoles = prefs.OrganizerRoleIds ?? [],
            Cs = ToGameForm(prefs.CounterStrikeChannelId, prefs.CounterStrikePingRoleId, prefs.CounterStrikeTeamIds,
                prefs.CounterStrikeTwitchChannels, prefs.CounterStrikeTeamAliases),
            Dota = ToGameForm(prefs.Dota2ChannelId, prefs.Dota2PingRoleId, prefs.Dota2TeamIds,
                prefs.Dota2TwitchChannels, prefs.Dota2TeamAliases)
        };
        var banner = TempData["SettingsMessage"] is string message
            ? new BannerModel("success", message, "New announcements use these settings right away.")
            : null;
        return SettingsView(guild, form, banner);
    }

    [HttpPost("/settings/{guildId}")]
    public async Task<IActionResult> SaveSettings(ulong guildId, ServerSettingsForm form)
    {
        if (await GetAdministratorAsync(guildId) is not { } guild)
            return await SettingsForbiddenAsync(guildId);

        TimeZoneInfo? zone = null;
        if (ModelState.GetFieldValidationState(nameof(form.TimeZone)) != Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid)
        {
            form.TimeZone = form.TimeZone.Trim();
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(form.TimeZone, out zone) || (!zone.HasIanaId && zone.Id != "UTC"))
                ModelState.AddModelError(nameof(form.TimeZone), "Enter an IANA time zone such as Europe/Kyiv or UTC.");
        }

        foreach (var (key, id) in new[] { ("ChannelId", (ulong?)form.ChannelId), ("Cs.Channel", form.Cs.Channel), ("Dota.Channel", form.Dota.Channel) })
        {
            if (id is { } channelId and not 0 && (guild.GetTextChannel(channelId) is not { } channel || !CanPostAnnouncements(guild, channel)))
                ModelState.AddModelError(key, "HappyMix can't post there. Choose a text channel where it can view, send messages and embed links.");
        }

        if (form.OrganizerRoles.Any(id => id == guildId || guild.GetRole(id) is null))
            ModelState.AddModelError(nameof(form.OrganizerRoles), "Choose roles from this server other than @everyone.");
        foreach (var (key, id) in new[] { ("Cs.PingRole", form.Cs.PingRole), ("Dota.PingRole", form.Dota.PingRole) })
        {
            if (id is { } roleId && (roleId == guildId || guild.GetRole(roleId) is null))
                ModelState.AddModelError(key, "Choose a role from this server other than @everyone.");
        }

        var cs = ParseGame(form.Cs, "Cs", options.Value.CounterStrikeTeamAliases);
        var dota = ParseGame(form.Dota, "Dota", options.Value.Dota2TeamAliases);
        if (!ModelState.IsValid || zone is null)
            return SettingsView(guild, form, null);

        var notifications = (form.Schedule ? EsportsNotifications.Schedule : 0) |
            (form.Reminders ? EsportsNotifications.Reminders : 0) |
            (form.Maps ? EsportsNotifications.Maps : 0) | (form.Results ? EsportsNotifications.Results : 0);
        var preferences = new GuildEsportsPreferences(cs.Teams, dota.Teams, notifications, form.ReminderMinutes,
            form.OrganizerRoles.Distinct().ToArray(), form.Cs.PingRole, form.Dota.PingRole, cs.Catalog, dota.Catalog,
            cs.Twitch, dota.Twitch, form.Cs.Channel, form.Dota.Channel, form.MatchStarts);

        var result = await sender.Send(new UpdateGuildSettingsCommand(guildId, options.Value.TimeZoneId,
            saved => saved with { ChannelId = form.ChannelId, TimeZoneId = zone.Id, Preferences = preferences }),
            HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            ModelState.AddModelError("", result.Error.Description);
            return SettingsView(guild, form, null);
        }
        TempData["SettingsMessage"] = "Server settings saved";
        return RedirectToAction(nameof(Settings), new { guildId });
    }

    private ViewResult SettingsView(SocketGuild guild, ServerSettingsForm form, BannerModel? banner)
    {
        var channels = guild.TextChannels
            .Where(channel => CanPostAnnouncements(guild, channel))
            .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(channel => new SelectListItem("#" + channel.Name, channel.Id.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        var roles = guild.Roles.Where(role => role.Id != guild.Id)
            .OrderByDescending(role => role.Position)
            .Select(role => new RoleOption(role.Id, role.Name,
                role.Colors.PrimaryColor.RawValue == 0 ? "#9aa1c4" : "#" + role.Colors.PrimaryColor.RawValue.ToString("x6", CultureInfo.InvariantCulture)))
            .ToArray();
        string deployment = guild.GetTextChannel(discordOptions.Value.AnnouncementChannelId) is { } fallback
            ? "#" + fallback.Name : "Bot default channel";
        string server = form.ChannelId == 0 ? deployment
            : channels.FirstOrDefault(item => item.Value == form.ChannelId.ToString(CultureInfo.InvariantCulture))?.Text
              ?? "a channel HappyMix can't post in";

        ViewData["Settings"] = new SettingsPage(guild.Id, guild.Name, channels, roles, deployment, server,
            GetGameDefaults(options.Value.CounterStrikeTeamIds, options.Value.CounterStrikeTeamAliases,
                discordOptions.Value.CounterStrikeTwitchChannels),
            GetGameDefaults(options.Value.Dota2TeamIds, options.Value.Dota2TeamAliases,
                discordOptions.Value.Dota2TwitchChannels),
            banner);
        return View("Settings", form);
    }

    private static GameDefaults GetGameDefaults(long[]? teams, Dictionary<string, long> catalog, string[] twitch)
    {
        var names = catalog.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);
        string teamText = teams switch
        {
            null => catalog.Count > 0 ? "Every team in the team catalog" : "Built-in team list",
            [] => "No teams",
            _ => Summarize(teams.Select(id => names.TryGetValue(id, out var name) ? name : id.ToString(CultureInfo.InvariantCulture)))
        };
        string twitchText = twitch.Length == 0 ? "No channels" : Summarize(twitch);
        string catalogText = catalog.Count == 0 ? "Built-in team list" : $"{catalog.Count} teams";
        return new GameDefaults(teamText, twitchText, catalogText);
    }

    private static string Summarize(IEnumerable<string> items)
    {
        var list = items.ToArray();
        return list.Length <= 4 ? string.Join(", ", list) : $"{string.Join(", ", list.Take(4))} +{list.Length - 4} more";
    }

    private async Task<IActionResult> SettingsForbiddenAsync(ulong guildId)
    {
        if (await GetDiscordMemberAsync(guildId) is null)
            return NotMember();

        return Forbidden(new ForbiddenPage("Admins only",
            $"Only Discord administrators of {discord.GetGuild(guildId)?.Name ?? "this server"} can change server settings. You can still set your availability.",
            $"/availability/{guildId}", "Go to availability"));
    }

    private static bool CanPostAnnouncements(SocketGuild guild, SocketTextChannel channel) =>
        channel.GetChannelType() == ChannelType.Text &&
        guild.CurrentUser.GetPermissions(channel) is { ViewChannel: true, SendMessages: true, EmbedLinks: true };

    private async Task<SocketGuild?> GetAdministratorAsync(ulong guildId) =>
        (await GetDiscordMemberAsync(guildId))?.GuildPermissions.Administrator == true ? discord.GetGuild(guildId) : null;

    private static GameSettingsForm ToGameForm(ulong? channel, ulong? role, long[]? teams, string[]? twitch,
        Dictionary<string, long>? catalog) => new()
    {
        Channel = channel,
        PingRole = role,
        TeamsMode = ModeOf(teams),
        Teams = teams is { Length: > 0 } ? string.Join(", ", teams) : null,
        TwitchMode = ModeOf(twitch),
        Twitch = twitch is { Length: > 0 } ? string.Join(", ", twitch) : null,
        CatalogMode = catalog is null ? GameSettingsForm.Inherit : GameSettingsForm.Custom,
        Catalog = catalog is null ? null : string.Join("\n", catalog.Select(pair =>
            $"{pair.Key}={pair.Value.ToString(CultureInfo.InvariantCulture)}"))
    };

    private static string ModeOf<T>(T[]? values) =>
        values is null ? GameSettingsForm.Inherit : values.Length == 0 ? GameSettingsForm.None : GameSettingsForm.Custom;

    private (long[]? Teams, string[]? Twitch, Dictionary<string, long>? Catalog) ParseGame(
        GameSettingsForm form, string prefix, IEnumerable<KeyValuePair<string, long>> defaultCatalog)
    {
        long[]? teams = null;
        string[]? twitch = null;
        Dictionary<string, long>? catalog = null;

        if (form.TwitchMode == GameSettingsForm.None)
            twitch = [];
        else if (form.TwitchMode == GameSettingsForm.Custom)
        {
            var items = Split(form.Twitch);
            string? invalid = items.FirstOrDefault(item => TwitchChannels.GetHandle(item) is null);
            if (invalid is not null)
                ModelState.AddModelError($"{prefix}.Twitch", $"\"{invalid}\" isn't a Twitch handle. Use handles or twitch.tv links, separated by commas.");
            else if (items.Length is 0 or > 5)
                ModelState.AddModelError($"{prefix}.Twitch", "Enter one to five Twitch handles or links, or choose None.");
            twitch = items.Select(TwitchChannels.GetHandle).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        }

        if (form.CatalogMode == GameSettingsForm.Custom)
        {
            catalog = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (form.Catalog ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.LastIndexOf('=');
                string name = separator < 0 ? "" : line[..separator].Trim();
                if (name.Length is 0 or > 40 || name.Contains(',') || name.Any(char.IsControl) ||
                    string.Equals(name, GameSettingsForm.Inherit, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, GameSettingsForm.None, StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ||
                    id <= 0 || !catalog.TryAdd(name, id))
                {
                    ModelState.AddModelError($"{prefix}.Catalog", $"Check \"{line}\". Use one unique Team name=123 entry per line.");
                    break;
                }
            }
        }

        if (form.TeamsMode == GameSettingsForm.None)
            teams = [];
        else if (form.TeamsMode == GameSettingsForm.Custom)
        {
            var aliases = catalog ?? new Dictionary<string, long>(defaultCatalog, StringComparer.OrdinalIgnoreCase);
            var items = Split(form.Teams);
            string? unknown = items.FirstOrDefault(item => !aliases.ContainsKey(item) &&
                !(long.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0));
            if (unknown is not null)
                ModelState.AddModelError($"{prefix}.Teams", $"\"{unknown}\" isn't in the team catalog or a team ID.");
            else if (items.Length is 0 or > 20)
                ModelState.AddModelError($"{prefix}.Teams", "Enter 1 to 20 team names or IDs, or choose None.");
            teams = items.Select(item => aliases.TryGetValue(item, out long id) ||
                long.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0)
                .Where(id => id > 0).Distinct().ToArray();
        }
        return (teams, twitch, catalog);
    }

    private static string[] Split(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
