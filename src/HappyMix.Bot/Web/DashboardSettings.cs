using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Esports;
using HappyMix.Domain.Esports;
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
    public bool CreateChannel { get; set; }
    [RegularExpression("en|uk|ru")] public string Language { get; set; } = "en";
    public bool DiscoverStreams { get; set; }
    [StringLength(200)] public string StreamLanguages { get; set; } = "";
    [StringLength(200)] public string StreamCountries { get; set; } = "";
    [StringLength(200)] public string StreamTags { get; set; } = "";
    [StringLength(100)] public string StreamPriority { get; set; } = "language,country,tags,viewers";
    public StreamDiscoveryPreferences Discovery() => new(DiscoverStreams, StreamLanguages ?? "", StreamCountries ?? "", StreamTags ?? "", StreamPriority ?? "");
    public ulong? PingRole { get; set; }
    public string TeamsMode { get; set; } = Inherit;
    /// <summary>One team per line: a name, or "Name #123" where the number is the team's ID.</summary>
    [StringLength(4000)] public string? Teams { get; set; }
    public string TwitchMode { get; set; } = Inherit;
    /// <summary>One Twitch handle or twitch.tv link per line.</summary>
    [StringLength(1000)] public string? Twitch { get; set; }
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
        var names = await GetTeamNamesAsync([.. prefs.CounterStrikeTeamIds ?? [], .. prefs.Dota2TeamIds ?? []]);
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
                prefs.CounterStrikeTwitchChannels, prefs.CounterStrikeTeamAliases, names),
            Dota = ToGameForm(prefs.Dota2ChannelId, prefs.Dota2PingRoleId, prefs.Dota2TeamIds,
                prefs.Dota2TwitchChannels, prefs.Dota2TeamAliases, names)
        };
        ApplyDiscovery(form.Cs, prefs.CounterStrikeLanguage, prefs.CounterStrikeDiscovery);
        ApplyDiscovery(form.Dota, prefs.Dota2Language, prefs.Dota2Discovery);
        var banner = TempData["SettingsMessage"] is string message
            ? new BannerModel("success", message, text["New_announcements_use_these_settings_right_away"])
            : null;
        return await SettingsViewAsync(guild, form, banner);
    }

    /// <summary>Team search for the "add a team" box. Admins only, like the page itself.</summary>
    [HttpGet("/settings/{guildId}/teams")]
    public async Task<IActionResult> SearchTeams(ulong guildId, string? game, string? q)
    {
        if (await GetAdministratorAsync(guildId) is null)
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await sender.Send(new SearchEsportsTeamsQuery(game == "dota" ? Esport.Dota2 : Esport.CounterStrike2, q ?? ""),
            HttpContext.RequestAborted);
        if (result.IsFailure)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = text.Message(result.Error.Description).Value });
        return Json(result.Value.Take(10).Select(team => new
        {
            id = team.Id, name = team.Name, acronym = team.Acronym, location = team.Location, image = team.ImageUrl,
            line = TeamText.Format(team)
        }));
    }

    [HttpPost("/settings/{guildId}")]
    public async Task<IActionResult> SaveSettings(ulong guildId, ServerSettingsForm form, [FromServices] NotificationChannels channels)
    {
        if (await GetAdministratorAsync(guildId) is not { } guild)
            return await SettingsForbiddenAsync(guildId);

        TimeZoneInfo? zone = null;
        if (ModelState.GetFieldValidationState(nameof(form.TimeZone)) != Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid)
        {
            form.TimeZone = form.TimeZone.Trim();
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(form.TimeZone, out zone) || (!zone.HasIanaId && zone.Id != "UTC"))
                ModelState.AddModelError(nameof(form.TimeZone), text["Enter_an_IANA_time_zone_such_as_Europe_Kyiv_or_UTC"]);
        }

        foreach (var (key, id) in new[] { ("ChannelId", (ulong?)form.ChannelId), ("Cs.Channel", form.Cs.Channel), ("Dota.Channel", form.Dota.Channel) })
        {
            if (id is { } channelId and not 0 && (guild.GetTextChannel(channelId) is not { } channel || !CanPostAnnouncements(guild, channel)))
                ModelState.AddModelError(key, text["HappyMix_can_t_post_there_Choose_a_text_channel_where_it_can"]);
        }

        if (form.OrganizerRoles.Any(id => id == guildId || guild.GetRole(id) is null))
            ModelState.AddModelError(nameof(form.OrganizerRoles), text["Choose_roles_from_this_server_other_than_everyone"]);
        foreach (var (key, id) in new[] { ("Cs.PingRole", form.Cs.PingRole), ("Dota.PingRole", form.Dota.PingRole) })
        {
            if (id is { } roleId && (roleId == guildId || guild.GetRole(roleId) is null))
                ModelState.AddModelError(key, text["Choose_a_role_from_this_server_other_than_everyone"]);
        }

        var cs = await ParseGameAsync(form.Cs, "Cs", Esport.CounterStrike2, options.Value.CounterStrikeTeamAliases);
        var dota = await ParseGameAsync(form.Dota, "Dota", Esport.Dota2, options.Value.Dota2TeamAliases);
        foreach (var (key, gameForm) in new[] { ("Cs", form.Cs), ("Dota", form.Dota) })
            if (!gameForm.Discovery().IsValid)
                ModelState.AddModelError(key + ".StreamPriority", text["Streams_PriorityHelp"]);
        if (!ModelState.IsValid || zone is null)
            return await SettingsViewAsync(guild, form, null);

        try
        {
            if (form.Cs.CreateChannel) form.Cs.Channel = (await channels.FindOrCreateAsync(guild, "cs2-announcements")).Id;
            if (form.Dota.CreateChannel) form.Dota.Channel = (await channels.FindOrCreateAsync(guild, "dota2-announcements")).Id;
        }
        catch (Discord.Net.HttpException)
        {
            ModelState.AddModelError("", text["Notifications_ChannelError"]);
            return await SettingsViewAsync(guild, form, null);
        }
        var notifications = (form.Schedule ? EsportsNotifications.Schedule : 0) |
            (form.Reminders ? EsportsNotifications.Reminders : 0) |
            (form.Maps ? EsportsNotifications.Maps : 0) | (form.Results ? EsportsNotifications.Results : 0);
        var preferences = new GuildEsportsPreferences(cs.Teams, dota.Teams, notifications, form.ReminderMinutes,
            form.OrganizerRoles.Distinct().ToArray(), form.Cs.PingRole, form.Dota.PingRole, cs.Catalog, dota.Catalog,
            cs.Twitch, dota.Twitch, form.Cs.Channel, form.Dota.Channel, form.MatchStarts, form.Cs.Language, form.Dota.Language, form.Cs.Discovery(), form.Dota.Discovery());

        var result = await sender.Send(new UpdateGuildSettingsCommand(guildId, options.Value.TimeZoneId,
            saved => saved with { ChannelId = form.ChannelId, TimeZoneId = zone.Id, Preferences = preferences }),
            HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            ModelState.AddModelError("", text.Message(result.Error.Description));
            return await SettingsViewAsync(guild, form, null);
        }
        TempData["SettingsMessage"] = text["Server_settings_saved"].Value;
        return RedirectToAction(nameof(Settings), new { guildId });
    }

    private static void ApplyDiscovery(GameSettingsForm form, string language, StreamDiscoveryPreferences? discovery)
    {
        form.Language = language;
        discovery ??= new();
        form.DiscoverStreams = discovery.Enabled;
        form.StreamLanguages = discovery.Languages;
        form.StreamCountries = discovery.Countries;
        form.StreamTags = discovery.Tags;
        form.StreamPriority = discovery.Priority;
    }

    private async Task<ViewResult> SettingsViewAsync(SocketGuild guild, ServerSettingsForm form, BannerModel? banner)
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
            ? "#" + fallback.Name : text["Bot_default_channel"];
        string server = form.ChannelId == 0 ? deployment
            : channels.FirstOrDefault(item => item.Value == form.ChannelId.ToString(CultureInfo.InvariantCulture))?.Text
              ?? text["A_channel_HappyMix_can_t_post_in"];

        long[] csDefaults = options.Value.DefaultCounterStrikeTeams, dotaDefaults = options.Value.DefaultDota2Teams;
        // Names for the default lists and every team typed into the form, so the editor can show names and logos.
        var typed = new[] { form.Cs.Teams, form.Dota.Teams }.SelectMany(Lines)
            .Select(line => TeamText.TryGetId(line, out long id) ? id : 0).Where(id => id > 0);
        var known = await GetTeamNamesAsync([.. csDefaults, .. dotaDefaults, .. typed]);
        ViewData["Settings"] = new SettingsPage(guild.Id, guild.Name, channels, roles, deployment, server,
            GetGameDefaults(csDefaults, options.Value.CounterStrikeTeamAliases, discordOptions.Value.CounterStrikeTwitchChannels, known),
            GetGameDefaults(dotaDefaults, options.Value.Dota2TeamAliases, discordOptions.Value.Dota2TwitchChannels, known),
            banner, known.Values.ToArray());
        return View("Settings", form);
    }

    private async Task<Dictionary<long, EsportsTeam>> GetTeamNamesAsync(long[] ids)
    {
        var result = await sender.Send(new GetEsportsTeamsQuery(ids), HttpContext.RequestAborted);
        return (result.IsSuccess ? result.Value : []).ToDictionary(team => team.Id);
    }

    private static string TeamLine(long id, Dictionary<long, EsportsTeam> known, Dictionary<string, long>? catalog) =>
        known.TryGetValue(id, out var team) ? TeamText.Format(team)
        : catalog?.FirstOrDefault(pair => pair.Value == id).Key is { } alias ? $"{alias} #{id}"
        : $"#{id}";

    private GameDefaults GetGameDefaults(long[] teams, Dictionary<string, long> catalog, string[] twitch,
        Dictionary<long, EsportsTeam> known)
    {
        string Name(long id) => known.TryGetValue(id, out var team) ? team.Name
            : catalog.FirstOrDefault(pair => pair.Value == id).Key ?? $"#{id}";
        string teamText = teams.Length == 0 ? text["No_teams"] : Summarize(teams.Select(Name));
        string twitchText = twitch.Length == 0 ? text["No_channels"] : Summarize(twitch);
        string catalogText = catalog.Count == 0 ? text["Built_in_team_list"] : text["Arg0_teams", catalog.Count];
        return new GameDefaults(teamText, twitchText, catalogText,
            teams.Select(id => TeamLine(id, known, catalog)).ToArray(),
            twitch.Select(TwitchChannels.GetHandle).OfType<string>().ToArray());
    }

    private string Summarize(IEnumerable<string> items)
    {
        var list = items.ToArray();
        return list.Length <= 4 ? string.Join(", ", list) : $"{string.Join(", ", list.Take(4))} {text["Arg0_more", list.Length - 4]}";
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
        Dictionary<string, long>? catalog, Dictionary<long, EsportsTeam> known) => new()
    {
        Channel = channel,
        PingRole = role,
        TeamsMode = ModeOf(teams),
        Teams = teams is { Length: > 0 } ? string.Join("\n", teams.Select(id => TeamLine(id, known, catalog))) : null,
        TwitchMode = ModeOf(twitch),
        Twitch = twitch is { Length: > 0 } ? string.Join("\n", twitch) : null,
        CatalogMode = catalog is null ? GameSettingsForm.Inherit : GameSettingsForm.Custom,
        Catalog = catalog is null ? null : string.Join("\n", catalog.Select(pair =>
            $"{pair.Key}={pair.Value.ToString(CultureInfo.InvariantCulture)}"))
    };

    private static string ModeOf<T>(T[]? values) =>
        values is null ? GameSettingsForm.Inherit : values.Length == 0 ? GameSettingsForm.None : GameSettingsForm.Custom;

    private async Task<(long[]? Teams, string[]? Twitch, Dictionary<string, long>? Catalog)> ParseGameAsync(
        GameSettingsForm form, string prefix, Esport game, IEnumerable<KeyValuePair<string, long>> defaultCatalog)
    {
        long[]? teams = null;
        string[]? twitch = null;
        Dictionary<string, long>? catalog = null;

        if (form.TwitchMode == GameSettingsForm.None)
            twitch = [];
        else if (form.TwitchMode == GameSettingsForm.Custom)
        {
            var items = Lines(form.Twitch)
                .SelectMany(line => line.Split([',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
            string? invalid = items.FirstOrDefault(item => TwitchChannels.GetHandle(item) is null);
            if (invalid is not null)
                ModelState.AddModelError($"{prefix}.Twitch", text["Arg0_isn_t_a_Twitch_channel_Use_a_handle_like_shroud_or", invalid]);
            else if (items.Length == 0 || items.Length > discordOptions.Value.MaxTwitchChannelsPerGame)
                ModelState.AddModelError($"{prefix}.Twitch", text["Settings_TwitchLimit", discordOptions.Value.MaxTwitchChannelsPerGame]);
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
                    ModelState.AddModelError($"{prefix}.Catalog", text["Check_Arg0_Use_one_unique_alias_123_entry_per_line", line]);
                    break;
                }
            }
        }

        if (form.TeamsMode == GameSettingsForm.None)
            teams = [];
        else if (form.TeamsMode == GameSettingsForm.Custom)
        {
            var aliases = catalog ?? new Dictionary<string, long>(defaultCatalog, StringComparer.OrdinalIgnoreCase);
            var items = Lines(form.Teams);
            var ids = new List<long>();
            if (items.Length == 0 || items.Length > discordOptions.Value.MaxFollowedTeamsPerGame)
                ModelState.AddModelError($"{prefix}.Teams", text["Settings_TeamLimit", discordOptions.Value.MaxFollowedTeamsPerGame]);
            else
            {
                // A catalog alias wins, then the provider's team search, so a plain name like "Vitality" is enough.
                foreach (string item in items)
                {
                    if (aliases.TryGetValue(item, out long alias))
                    {
                        ids.Add(alias);
                        continue;
                    }
                    var resolved = await sender.Send(new ResolveEsportsTeamQuery(game, item), HttpContext.RequestAborted);
                    string? problem = resolved switch
                    {
                        { IsFailure: true } => text.Message(resolved.Error.Description).Value,
                        { Value.Team: null, Value.Candidates.Count: 0 } => text[
                            "No_Arg0_team_called_Arg1_was_found_Check_the_spelling_or_add",
                            game.DisplayName(), item].Value,
                        { Value.Team: null } => text[
                            "Several_Arg0_teams_match_Arg1_Arg2_Pick_one_from_the_search_or",
                            game.DisplayName(), item, string.Join(", ", resolved.Value.Candidates.Select(TeamText.Describe))].Value,
                        _ => null
                    };
                    if (problem is not null)
                    {
                        ModelState.AddModelError($"{prefix}.Teams", problem);
                        break;
                    }
                    ids.Add(resolved.Value.Team!.Id);
                }
            }
            teams = ids.Distinct().ToArray();
        }
        return (teams, twitch, catalog);
    }

    private static string[] Lines(string? value) =>
        (value ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
