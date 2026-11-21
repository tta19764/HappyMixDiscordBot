using System.Globalization;
using System.Text.Encodings.Web;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace HappyMix.Bot.Web;

public sealed record BannerModel(string Kind, string Title, string? Text = null, bool Focus = true,
    IReadOnlyList<(string Label, string Href)>? Links = null);

public sealed record ServerNavModel(ulong GuildId, string GuildName, string Active, bool CanConfigure,
    string Title, string Lead);

/// <summary>What the side panel needs: the server, the active section and whether to show the admin links.</summary>
public sealed record SideNav(ulong GuildId, string GuildName, string Active, bool CanConfigure)
{
    public const string Key = "Side";

    /// <summary>Records the side panel context for the layout (views render before their layout).</summary>
    public static void Set(Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary viewData, ulong guildId, string guildName, string active, bool canConfigure)
    {
        viewData[Key] = new SideNav(guildId, guildName, active, canConfigure);
        viewData["NavGuildName"] = guildName;
    }

    /// <summary>The same from a partial: partials get their own copy of ViewData, so the request carries it to the layout.</summary>
    public static void Set(Microsoft.AspNetCore.Http.HttpContext context, ulong guildId, string guildName, string active, bool canConfigure) =>
        context.Items[Key] = new SideNav(guildId, guildName, active, canConfigure);

    public static SideNav? Get(Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary viewData, Microsoft.AspNetCore.Http.HttpContext context) =>
        viewData[Key] as SideNav ?? context.Items[Key] as SideNav;
}

public sealed record ForbiddenPage(string Title, string Text, string ActionUrl, string ActionLabel);

public sealed record DashboardServer(ulong Id, string Name, string? IconUrl, bool CanConfigure,
    string? TodayStatus, bool LastUsed);

public sealed record AvailabilityPreset(string Label, string Start, string End)
{
    public string Range => $"{Start}–{End}";
}

public sealed record UpcomingRow(Guid Id, string Title, string Meta, bool Tentative = false);

public sealed record WeekRow(DayOfWeek Day, IReadOnlyList<WeeklyAvailability> Windows, bool IsToday, bool IsEditing, bool IsSaved)
{
    public string Name => CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(Day);
    public string Anchor => Day.ToString().ToLowerInvariant();
    public string ShortName => AvailabilityFormat.ShortDay(Day);
    public string NextShortName => AvailabilityFormat.ShortDay((DayOfWeek)(((int)Day + 1) % 7));
}

public sealed record AvailabilityForm(string? Start = null, string? End = null, string? Offset = null, bool AllDay = false, bool Tentative = false)
{
    /// <summary>Ranges submitted from the weekly day editor, kept when saving fails.</summary>
    public IReadOnlyList<HappyMix.Application.Guilds.WeeklyRangeInput>? Ranges { get; init; }
}

public sealed record AvailabilityPage(
    ulong GuildId,
    string GuildName,
    bool CanConfigure,
    string View,
    string TimeZone,
    string TimeZoneOffset,
    string TodayLabel,
    IReadOnlyList<UpcomingRow> Upcoming,
    IReadOnlyList<WeekRow> Week,
    IReadOnlyList<AvailabilityPreset> Presets,
    BannerModel? Banner,
    AvailabilityForm Form)
{
    public DateOnly SelectedDate { get; init; }
    public IReadOnlySet<DateOnly> ConfiguredDates { get; init; } = new HashSet<DateOnly>();
    public bool IsWeekly => View == "weekly";
    public bool IsMembers => View == "members";
    /// <summary>Everyone's availability for the "Members" view: the selected date's board and the month's counts.</summary>
    public MembersBoardModel? Members { get; init; }
    /// <summary>Event dates the member accepted, in the server time zone, for the timetable and calendar.</summary>
    public IReadOnlyList<EventBlock> EventBlocks { get; init; } = [];
    /// <summary>Dates the member accepted (pending and confirmed, plus canceled ones they expected) and invitations waiting.</summary>
    public IReadOnlyList<EventRow> MyEvents { get; init; } = [];
    public int EventInvitations { get; init; }
    public int WeeklyCount => Week.Count(row => row.Windows.Count > 0);
    /// <summary>The viewer's do-not-disturb, for the Dates and Weekly views.</summary>
    public HappyMix.Application.Quiet.QuietOverview? DoNotDisturb { get; init; }
    public bool IsEditing => Week.Any(row => row.IsEditing);
}

/// <summary>An accepted event date drawn on the member's own availability. Hours run 0–24 on <see cref="Date"/>.</summary>
public sealed record EventBlock(DateOnly Date, double StartHour, double EndHour, string Title, string Time, bool Confirmed, string Url);

/// <summary>
/// A members × hours board. <see cref="Selectable"/> adds the event form's time slider on top; <see cref="DayUrl"/>
/// links calendar days when the month counts are shown.
/// </summary>
public sealed record MembersBoardModel(HappyMix.Application.Guilds.MembersAvailability Data, MemberNames Names, ulong Viewer,
    bool Selectable = false, Func<DateOnly, string>? DayUrl = null, ILookup<ulong, BoardEvent>? Events = null);

public sealed record RoleOption(ulong Id, string Name, string Color);

public sealed record SettingsPage(
    ulong GuildId,
    string GuildName,
    SelectListItem[] Channels,
    RoleOption[] Roles,
    string DeploymentChannel,
    string ServerChannel,
    GameDefaults CounterStrike,
    GameDefaults Dota,
    BannerModel? Banner,
    HappyMix.Application.Abstractions.Esports.EsportsTeam[] KnownTeams)
{
    public CommunityView Community { get; init; } = new("English", "", "", "");
    /// <summary>Where match posts go with the values on the page, one entry per channel.</summary>
    public RouteSummary[] Routes { get; init; } = [];
}

/// <summary>The deployment's community defaults, as shown next to "use community preferences".</summary>
public sealed record CommunityView(string Language, string StreamLanguages, string StreamCountries, string StreamTags);

/// <summary>One announcement channel, the games that post there, and anything worth fixing about it.</summary>
public sealed record RouteSummary(string Channel, RouteGame[] Games, string[] Warnings, RouteFix[] Fixes);
public sealed record RouteGame(string Name, string Section, string Language);
/// <summary>A one-click fix: tick <see cref="CheckboxId"/> to give <see cref="Game"/> its own channel.</summary>
public sealed record RouteFix(string Game, string CheckboxId);

/// <summary>What a game inherits: summaries for display, and the lines a "Custom" list starts from.</summary>
public sealed record GameDefaults(string Teams, string Twitch, string Catalog, string[] TeamLines, string[] TwitchLines);

public static class AvailabilityFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string ShortDay(DayOfWeek day) => CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName(day);

    public static double Percent(TimeOnly time) => time.ToTimeSpan().TotalMinutes / 1440 * 100;

    public static string Css(double percent) => percent.ToString("0.##", Culture) + "%";

    public static string Time(TimeOnly time) => time.ToString("HH:mm", Culture);

    public static string Duration(TimeOnly start, TimeOnly end)
    {
        int span = ((int)(end - start).TotalMinutes % 1440 + 1440) % 1440;
        if (span == 0)
            span = 1440;
        return span % 60 == 0 ? $"{span / 60}h" : $"{span / 60}h {span % 60}m";
    }

    /// <summary>True when a range runs from local midnight to the next local midnight.</summary>
    public static bool IsFullDay(DateTime start, DateTime end) =>
        start.TimeOfDay == TimeSpan.Zero && end == start.Date.AddDays(1);

    /// <summary>"Today 17:00 → Tomorrow 00:30", relative to <paramref name="today"/>.</summary>
    public static string Range(DateTime start, DateTime end, DateOnly today) =>
        $"{Day(DateOnly.FromDateTime(start), today)} {start.ToString("HH:mm", Culture)} → " +
        $"{(DateOnly.FromDateTime(end) == DateOnly.FromDateTime(start)
            ? "" : Day(DateOnly.FromDateTime(end), today) + " ")}{end.ToString("HH:mm", Culture)}";

    public static string Day(DateOnly date, DateOnly today) =>
        date == today ? "Today" : date == today.AddDays(1) ? "Tomorrow" : ShortDay(date.DayOfWeek);

    public static string Offset(TimeSpan offset) =>
        "UTC" + (offset < TimeSpan.Zero ? "−" : "+") + offset.Duration().ToString(@"hh\:mm", Culture);
}

/// <summary>
/// HappyMix's own icon set: a 24 px grid, 1.75 px strokes, square caps and mitred joins, straight lines first and curves
/// only where the object needs one. Icons mark navigation, tabs, headings, cards and tags; buttons are words only,
/// apart from the basic controls (collapse, switch server, theme, language, sign in and out).
/// </summary>
public static class DashboardIcons
{
    private static readonly Dictionary<string, string[]> Shapes = new(StringComparer.Ordinal)
    {
        // Sections
        ["availability"] = ["M4 6h16v14H4z", "M4 10h16", "M8 3v4", "M16 3v4", "M8 14h3", "M13 14h3", "M8 17h3"],
        ["events"] = ["M6 21V3", "M6 4h12l-3 4 3 4H6"],
        ["news"] = ["M4 5h12v14H4z", "M16 9h4v10h-4", "M7 9h6", "M7 12h6", "M7 15h4"],
        ["groups"] = ["M6 5h4v4H6z", "M4 19v-6h8v6", "M15 7h4v4h-4z", "M14 19v-5h7v5"],
        ["votes"] = ["M4 20h16", "M6 20v-8h3v8", "M11 20V6h3v14", "M16 20v-5h3v5"],
        ["links"] = ["M10 7H5v10h5", "M14 7h5v10h-5", "M8 12h8"],
        ["settings"] = ["M4 7h9", "M17 7h3", "M13 4v6", "M4 17h3", "M11 17h9", "M7 14v6"],
        ["logs"] = ["M5 4h14v16H5z", "M8 8h8", "M8 12h8", "M8 16h5"],
        ["guide"] = ["M5 4h10l4 4v12H5z", "M15 4v4h4", "M9 13h6", "M9 16h4"],
        ["overview"] = ["M4 4h7v7H4z", "M13 4h7v4h-7z", "M13 10h7v10h-7z", "M4 13h7v7H4z"],
        ["watch"] = ["M3 5h18v12H3z", "M8 21h8", "M10 8v6l5-3z"],
        ["live"] = ["M3 12h4l2-5 4 10 2-5h6"],
        // Basic controls
        ["collapse"] = ["M3 4h18v16H3z", "M9 4v16", "M16 9l-3 3 3 3"],
        ["expand"] = ["M3 4h18v16H3z", "M9 4v16", "M13 9l3 3-3 3"],
        ["menu"] = ["M4 6h16", "M4 12h16", "M4 18h16"],
        ["switch"] = ["M8 9l4-4 4 4", "M8 15l4 4 4-4"],
        ["logout"] = ["M13 4H5v16h8", "M10 12h10", "M17 9l3 3-3 3"],
        ["login"] = ["M11 4h8v16h-8", "M4 12h10", "M11 9l3 3-3 3"],
        ["sun"] = ["M9 9h6v6H9z", "M12 3v3", "M12 18v3", "M3 12h3", "M18 12h3", "M5.6 5.6l2 2", "M16.4 16.4l2 2", "M5.6 18.4l2-2", "M16.4 7.6l2-2"],
        ["moon"] = ["M14 3h-1a9 9 0 0 0 0 18h1a9 9 0 0 0 7-4 8 8 0 0 1-7-14z"],
        ["monitor"] = ["M3 5h18v11H3z", "M8 20h8", "M12 16v4"],
        ["globe"] = ["M4 19 9 5l5 14", "M6 14h6", "M15 8h6", "M18 6v2", "M20 8c0 4-2 7-5 9", "M16 12c1 2 3 4 5 4"],
        // Tags
        ["games"] = ["M3 8h18v9H3z", "M7 10.5v4", "M5 12.5h4", "M15 11.5h1", "M18 13.5h1"],
        ["series"] = ["M4 7h16v12H4z", "M9 3l3 4 3-4"],
        ["films"] = ["M5 4h14v16H5z", "M8 4v16", "M16 4v16", "M5 9h3", "M5 15h3", "M16 9h3", "M16 15h3"],
        ["anime"] = ["M12 3l2.2 6.8L21 12l-6.8 2.2L12 21l-2.2-6.8L3 12l6.8-2.2z"],
        ["hash"] = ["M10 4 8 20", "M16 4l-2 16", "M5 9h15", "M4 15h15"],
        // Headings, cards and details
        ["clock"] = ["M4 4h16v16H4z", "M12 8v5h4"],
        ["zone"] = ["M12 3l7 4v10l-7 4-7-4V7z", "M5 7l7 4 7-4", "M12 11v10"],
        ["request"] = ["M7 5h4v4H7z", "M5 19v-6h8v6", "M18 9v6", "M15 12h6"],
        ["idea"] = ["M8 4h8v8l-2 3h-4l-2-3z", "M10 18h4", "M11 21h2"],
        ["done"] = ["M4 4h16v16H4z", "M8 12l3 3 5-6"],
        ["repeat"] = ["M4 9h13l-3-3", "M20 15H7l3 3"],
        ["voice"] = ["M10 4h4v9h-4z", "M6 11v2a6 6 0 0 0 12 0v-2", "M12 19v2"],
        ["search"] = ["M5 5h10v10H5z", "M15 15l5 5"],
        ["platform"] = ["M3 5h18v11H3z", "M8 20h8", "M12 16v4"],
        ["lock"] = ["M6 11h12v9H6z", "M9 11V7h6v4"],
        ["door"] = ["M6 3h12v18H6z", "M14 12h1"],
        ["bell"] = ["M6 17v-7a6 6 0 0 1 12 0v7", "M4 17h16", "M10 20h4"],
        ["pencil"] = ["M4 20h4L19 9l-4-4L4 16z", "M13 7l4 4"],
        ["alert"] = ["M12 4l9 16H3z", "M12 10v4", "M12 17v1"],
        ["trophy"] = ["M7 4h10v6a5 5 0 0 1-10 0z", "M7 6H4v3h3", "M17 6h3v3h-3", "M12 15v4", "M8 20h8"],
        ["shield"] = ["M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"],
        ["info"] = ["M4 4h16v16H4z", "M12 11v6", "M12 7v1"],
        ["bang"] = ["M12 6v8", "M12 17v1"],
        ["calendar"] = ["M4 6h16v14H4z", "M4 10h16", "M8 3v4", "M16 3v4"],
        ["users"] = ["M6 5h4v4H6z", "M4 19v-6h8v6", "M15 7h4v4h-4z", "M14 19v-5h7v5"],
        ["sliders"] = ["M4 7h9", "M17 7h3", "M13 4v6", "M4 17h3", "M11 17h9", "M7 14v6"],
        ["twitch"] = ["M5 4h15v10l-4 4h-4l-3 3v-3H5z", "M11 8v4", "M15 8v4"],
        // Small marks used inside controls and lists
        ["check"] = ["M5 12l5 5 9-10"],
        ["plus"] = ["M12 5v14", "M5 12h14"],
        ["minus"] = ["M5 12h14"],
        ["chevron"] = ["M6 9l6 6 6-6"],
        ["back"] = ["M19 12H5", "M11 6l-6 6 6 6"],
        ["arrow"] = ["M5 12h14", "M13 6l6 6-6 6"],
        ["trash"] = ["M4 7h16", "M10 11v6", "M14 11v6", "M6 7l1 13h10l1-13", "M9 7V4h6v3"],
        ["refresh"] = ["M20 12a8 8 0 1 1-2.3-5.7", "M20 4v5h-5"],
        ["copy"] = ["M8 8h12v12H8z", "M16 8V4H4v12h4"],
        ["grip"] = ["M9 6h1", "M15 6h1", "M9 12h1", "M15 12h1", "M9 18h1", "M15 18h1"]
    };

    private static readonly Dictionary<string, string> Paths = Shapes.ToDictionary(pair => pair.Key,
        pair => string.Concat(pair.Value.Select(d => $"<path d=\"{d}\"/>")), StringComparer.Ordinal);

    public static bool Has(string name) => Paths.ContainsKey(name);

    public static IHtmlContent Render(string name, string? style = null, string? cssClass = null) => new HtmlString(
        $"<svg class=\"hm-icon{(cssClass is null ? "" : " " + cssClass)}\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\"{(style is null ? "" : $" style=\"{style}\"")}>{Paths[name]}</svg>");

    /// <summary>The icon for a group tag: games, series, films and anime have their own; any other tag is a hash.</summary>
    public static IHtmlContent Tag(string tag) => Render(tag is "games" or "series" or "films" or "anime" ? tag : "hash");

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => char.IsLetterOrDigit(word[0])).ToArray();
        string initials = string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
        return initials.Length > 0 ? initials : "#";
    }
}

public sealed record TimeRangeModel(string Id, string DayLabel, string? Start, string? End, string? Offset,
    string Zone, string ZoneOffset, bool AllDay = false)
{
    /// <summary>"18:30" → ("18", "30"); null for anything that isn't HH:mm.</summary>
    public static (string Hour, string Minute)? Split(string? time) =>
        time is { Length: 5 } && time[2] == ':' && TimeOnly.TryParseExact(time, "HH:mm", out _) ? (time[..2], time[3..]) : null;

    /// <summary>Combines the hour and minute selects; an empty minute means 00. Returns null without an hour.</summary>
    public static string? Join(string? hour, string? minute) =>
        string.IsNullOrWhiteSpace(hour) ? null : $"{hour.Trim()}:{(string.IsNullOrWhiteSpace(minute) ? "00" : minute.Trim())}";
}

/// <summary>Field-level validation markup: errors render beside the control they belong to.</summary>
public static class FormErrors
{
    public static string? Message(ModelStateDictionary state, string key) =>
        state.TryGetValue(key, out var entry) && entry.Errors.Count > 0 ? entry.Errors[0].ErrorMessage : null;

    public static string Id(string key) => key.Replace('.', '_');

    public static string? Invalid(ModelStateDictionary state, string key) => Message(state, key) is null ? null : "true";

    public static string? DescribedBy(ModelStateDictionary state, string key, string? helpId = null)
    {
        string ids = string.Join(' ', new[] { helpId, Message(state, key) is null ? null : Id(key) + "-error" }.OfType<string>());
        return ids.Length == 0 ? null : ids;
    }

    public static IHtmlContent Render(ModelStateDictionary state, string key, string? style = null) =>
        Message(state, key) is { } message
            ? new HtmlString($"<p class=\"hm-error\" id=\"{Id(key)}-error\"{(style is null ? "" : $" style=\"{style}\"")}>" +
                $"{DashboardIcons.Render("alert")}{HtmlEncoder.Default.Encode(message)}</p>")
            : HtmlString.Empty;

    public static IHtmlContent SourceBadge(string mode, Microsoft.Extensions.Localization.IStringLocalizer text)
    {
        string[] labels = [text["Inherited"], text["Custom"], text["Off"]];
        string names = HtmlEncoder.Default.Encode(string.Join("|", labels));
        var (label, tone) = mode switch
        {
            GameSettingsForm.Custom => (labels[1], " hm-badge--custom"),
            GameSettingsForm.None => (labels[2], ""),
            _ => (labels[0], " hm-badge--inherit")
        };
        return new HtmlString($"<span class=\"hm-badge{tone}\" data-source-badge data-labels=\"{names}\">{HtmlEncoder.Default.Encode(label)}</span>");
    }
}

/// <summary>The weekly day editor: the day's ranges in one time zone, edited together and saved at once.</summary>
public sealed record WeekDayEditorModel(WeekRow Row, IReadOnlyList<HappyMix.Application.Guilds.WeeklyRangeInput> Ranges,
    IReadOnlyList<WeeklyAvailability> OtherZones, string Zone, string ServerZone, string ServerZoneOffset, string BaseUrl, string WeeklyUrl)
{
    /// <summary>Hours shown on the track: the whole day plus the first six hours of the next one, for overnight ranges.</summary>
    public const int TrackHours = 30;
    public bool CustomZone => Zone != ServerZone;
}

public sealed record WeekRangeItemModel(WeekRow Row, int Index, string? Start, string? End, bool Tentative, bool Blank = false)
{
    public string Id => $"{Row.Anchor}-range-{(Index < 0 ? "new" : Index.ToString(CultureInfo.InvariantCulture))}";
}

/// <summary>The saved colour theme: "light", "dark", or null to follow the system setting.</summary>
public static class DashboardTheme
{
    public const string Cookie = "hm_theme";

    public static string? Read(HttpRequest request) => request.Cookies[Cookie] is "light" or "dark" ? request.Cookies[Cookie] : null;
}

/// <summary>The command guide, plus the last server the member opened so the page can lead back to it.</summary>
public sealed record InfoPage(HappyMix.Bot.Interactions.CommandGuide[] Commands, ulong? GuildId, string? GuildName);

/// <summary>A one-per-line list field that the dashboard shows as rows with remove buttons and an add box.</summary>
public sealed record ListEditorModel(string Kind, string Id, string Name, string? Value, int Max, int MaxLength,
    string Label, string EmptyText, string AddPlaceholder, string TextPlaceholder, string Help, string[] DefaultLines,
    string? Invalid, string? DescribedBy, string? SearchUrl = null, string? KnownJson = null);

/// <param name="ReturnUrl">A local page to go back to after signing in.</param>
/// <param name="SignedOut">Show the "You're signed out" note.</param>
public sealed record LoginPage(string ReturnUrl, bool SignedOut);

/// <summary>The do-not-disturb card: weekly hours on the Weekly view, dates on the Dates view.</summary>
/// <param name="Url">The form address, <c>/availability/{guild}/dnd</c>.</param>
/// <param name="TimeZone">The zone new ranges are entered in (the page's zone).</param>
/// <param name="Date">The date the Dates form starts on.</param>
public sealed record DoNotDisturbModel(HappyMix.Application.Quiet.QuietOverview Overview, string Url, bool Weekly, string TimeZone, DateOnly Date);
