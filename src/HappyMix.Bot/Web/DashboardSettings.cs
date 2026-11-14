using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.PandaScore;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Web;

public sealed class ServerSettingsForm
{
    public ulong ChannelId { get; set; }
    [Required, StringLength(100)] public string TimeZone { get; set; } = "UTC";
    [Range(1, 1440)] public int ReminderMinutes { get; set; } = 30;
    public bool Schedule { get; set; }
    public bool Reminders { get; set; }
    public bool Maps { get; set; }
    public bool Results { get; set; }
    public bool MatchStarts { get; set; }
    public ulong[] OrganizerRoles { get; set; } = [];
    public GameSettingsForm Cs { get; set; } = new();
    public GameSettingsForm Dota { get; set; } = new();
}

public sealed class GameSettingsForm
{
    public ulong? Channel { get; set; }
    public ulong? PingRole { get; set; }
    [StringLength(500)] public string? Teams { get; set; }
    [StringLength(500)] public string? Twitch { get; set; }
    [StringLength(16000)] public string? Catalog { get; set; }
}

public sealed partial class DashboardController
{
    [HttpGet("/settings/{guildId}")]
    public async Task<IActionResult> Settings(ulong guildId)
    {
        if (await GetAdministratorAsync(guildId) is not { } guild)
            return StatusCode(StatusCodes.Status403Forbidden);

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
        return SettingsView(guild, form);
    }

    [HttpPost("/settings/{guildId}")]
    public async Task<IActionResult> SaveSettings(ulong guildId, ServerSettingsForm form)
    {
        if (await GetAdministratorAsync(guildId) is not { } guild)
            return StatusCode(StatusCodes.Status403Forbidden);

        if (!ModelState.IsValid)
            return SettingsView(guild, form);

        form.TimeZone = form.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(form.TimeZone, out var zone) || (!zone.HasIanaId && zone.Id != "UTC"))
            ModelState.AddModelError(nameof(form.TimeZone), "Enter an IANA time zone such as Europe/Kyiv or UTC.");

        foreach (ulong id in new ulong?[] { form.ChannelId, form.Cs.Channel, form.Dota.Channel }.OfType<ulong>().Where(id => id != 0))
        {
            if (guild.GetTextChannel(id) is not { } channel ||
                guild.CurrentUser.GetPermissions(channel) is not { ViewChannel: true, SendMessages: true, EmbedLinks: true })
                ModelState.AddModelError("", "Choose channels where the bot can view, send messages, and embed links.");
        }

        foreach (ulong id in form.OrganizerRoles.Concat(new ulong?[] { form.Cs.PingRole, form.Dota.PingRole }.OfType<ulong>()))
        {
            if (id == guildId || guild.GetRole(id) is null)
                ModelState.AddModelError("", "Choose roles from this server other than @everyone.");
        }

        var cs = ParseGame(form.Cs, "Counter-Strike 2", options.Value.CounterStrikeTeamAliases);
        var dota = ParseGame(form.Dota, "Dota 2", options.Value.Dota2TeamAliases);
        if (!ModelState.IsValid)
            return SettingsView(guild, form);

        var notifications = (form.Schedule ? EsportsNotifications.Schedule : 0) |
            (form.Reminders ? EsportsNotifications.Reminders : 0) |
            (form.Maps ? EsportsNotifications.Maps : 0) | (form.Results ? EsportsNotifications.Results : 0);
        var preferences = new GuildEsportsPreferences(cs.Teams, dota.Teams, notifications, form.ReminderMinutes,
            form.OrganizerRoles.Distinct().ToArray(), form.Cs.PingRole, form.Dota.PingRole, cs.Catalog, dota.Catalog,
            cs.Twitch, dota.Twitch, form.Cs.Channel, form.Dota.Channel, form.MatchStarts);

        var result = await sender.Send(new UpdateGuildSettingsCommand(guildId, options.Value.TimeZoneId,
            saved => saved with { ChannelId = form.ChannelId, TimeZoneId = zone!.Id, Preferences = preferences }),
            HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            ModelState.AddModelError("", result.Error.Description);
            return SettingsView(guild, form);
        }
        TempData["SettingsMessage"] = "Server settings saved.";
        return RedirectToAction(nameof(Settings), new { guildId });
    }

    private ViewResult SettingsView(SocketGuild guild, ServerSettingsForm form)
    {
        ViewData["Guild"] = guild;
        return View("Settings", form);
    }

    private async Task<SocketGuild?> GetAdministratorAsync(ulong guildId) =>
        (await GetDiscordMemberAsync(guildId))?.GuildPermissions.Administrator == true ? discord.GetGuild(guildId) : null;

    private static GameSettingsForm ToGameForm(ulong? channel, ulong? role, long[]? teams, string[]? twitch,
        Dictionary<string, long>? catalog) => new()
    {
        Channel = channel,
        PingRole = role,
        Teams = FormatSelection(teams),
        Twitch = FormatSelection(twitch),
        Catalog = catalog is null ? "default" : string.Join("\n", catalog.Select(pair =>
            $"{pair.Key}={pair.Value.ToString(CultureInfo.InvariantCulture)}"))
    };

    private static string FormatSelection<T>(T[]? values) =>
        values is null ? "default" : values.Length == 0 ? "none" : string.Join(", ", values);

    private (long[]? Teams, string[]? Twitch, Dictionary<string, long>? Catalog) ParseGame(
        GameSettingsForm form, string label, IEnumerable<KeyValuePair<string, long>> defaultCatalog)
    {
        long[]? teams = null;
        string[]? twitch = null;
        Dictionary<string, long>? catalog = null;
        if (!IsDefault(form.Twitch))
        {
            var items = SplitSelection(form.Twitch).Select(TwitchChannels.GetHandle).ToArray();
            if (items.Length > 5 || items.Any(handle => handle is null))
                ModelState.AddModelError("", $"{label}: enter up to five Twitch handles or links, default, or none.");
            twitch = items.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        }
        if (!IsDefault(form.Catalog))
        {
            catalog = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (form.Catalog ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.LastIndexOf('=');
                string name = separator < 0 ? "" : line[..separator].Trim();
                if (name.Length is 0 or > 40 || name.Contains(',') || name.Any(char.IsControl) ||
                    IsDefault(name) || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ||
                    id <= 0 || !catalog.TryAdd(name, id))
                {
                    ModelState.AddModelError("", $"{label}: use unique catalog entries in the form Team name=123.");
                    break;
                }
            }
        }
        if (!IsDefault(form.Teams))
        {
            var aliases = catalog ?? new Dictionary<string, long>(defaultCatalog, StringComparer.OrdinalIgnoreCase);
            var items = SplitSelection(form.Teams);
            teams = items.Select(item => aliases.TryGetValue(item, out long id) ||
                long.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0)
                .Distinct().ToArray();
            if (items.Length > 20 || teams.Any(id => id <= 0))
                ModelState.AddModelError("", $"{label}: enter up to 20 team names or positive team IDs, default, or none.");
        }
        return (teams, twitch, catalog);
    }

    private static bool IsDefault(string? text) => string.Equals(text?.Trim(), "default", StringComparison.OrdinalIgnoreCase);

    private static string[] SplitSelection(string? text) =>
        string.Equals(text?.Trim(), "none", StringComparison.OrdinalIgnoreCase) ? []
            : (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
