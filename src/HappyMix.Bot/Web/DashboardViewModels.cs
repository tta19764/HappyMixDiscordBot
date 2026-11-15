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
    /// <summary>Dates the member accepted (pending and confirmed, plus canceled ones they expected) and invitations waiting.</summary>
    public IReadOnlyList<EventRow> MyEvents { get; init; } = [];
    public int EventInvitations { get; init; }
    public int WeeklyCount => Week.Count(row => row.Windows.Count > 0);
    public bool IsEditing => Week.Any(row => row.IsEditing);
}

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
    HappyMix.Application.Abstractions.Esports.EsportsTeam[] KnownTeams);

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

public static class DashboardIcons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["chevron"] = "<path d=\"m6 9 6 6 6-6\"/>",
        ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"6.5\"/><path d=\"m20 20-4.2-4.2\"/>",
        ["twitch"] = "<path d=\"M5 3.5 3.5 7v12.5h4.5V22l2.5-2.5h3.5L20.5 13V3.5zM11 8v5M15.5 8v5\"/>",
        ["minus"] = "<path d=\"M5 12h14\"/>",
        ["back"] = "<path d=\"M19 12H5M11 6l-6 6 6 6\"/>",
        ["arrow"] = "<path d=\"M5 12h14M13 6l6 6-6 6\"/>",
        ["globe"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18\"/>",
        ["sun"] = "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4\"/>",
        ["monitor"] = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"12\" rx=\"2\"/><path d=\"M8 20h8M12 16v4\"/>",
        ["moon"] = "<path d=\"M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z\"/>",
        ["check"] = "<path d=\"m5 12 5 5 9-10\"/>",
        ["alert"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7.5v5.5M12 16.5h.01\"/>",
        ["bang"] = "<path d=\"M12 7.5v5.5M12 16.5h.01\"/>",
        ["trash"] = "<path d=\"M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3\"/>",
        ["pencil"] = "<path d=\"M4 20h4L19 9l-4-4L4 16v4zM14 6l4 4\"/>",
        ["sliders"] = "<path d=\"M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12\"/><circle cx=\"16\" cy=\"6\" r=\"2\"/><circle cx=\"10\" cy=\"12\" r=\"2\"/><circle cx=\"18\" cy=\"18\" r=\"2\"/>",
        ["calendar"] = "<rect x=\"3.5\" y=\"5\" width=\"17\" height=\"15\" rx=\"2\"/><path d=\"M3.5 10h17M8 3v4M16 3v4\"/>",
        ["clock"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3 2\"/>",
        ["plus"] = "<path d=\"M12 5v14M5 12h14\"/>",
        ["refresh"] = "<path d=\"M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5\"/>",
        ["shield"] = "<path d=\"M12 3l8 3v6c0 4.5-3.4 8-8 9-4.6-1-8-4.5-8-9V6z\"/>",
        ["info"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 11v5M12 8h.01\"/>",
        ["logout"] = "<path d=\"M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3M10 17l-5-5 5-5M5 12h11\"/>",
        ["users"] = "<circle cx=\"9\" cy=\"9\" r=\"3.5\"/><path d=\"M2.5 20a6.5 6.5 0 0 1 13 0M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6 6 0 0 1 3.5 6\"/>"
    };

    public static IHtmlContent Render(string name, string? style = null) => new HtmlString(
        $"<svg class=\"hm-icon\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\"{(style is null ? "" : $" style=\"{style}\"")}>{Paths[name]}</svg>");

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
