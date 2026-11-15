using System.Globalization;
using HappyMix.Application.Guilds;
using HappyMix.Bot.Services;

namespace HappyMix.Bot.Web;

/// <summary>Who is looking at the page: the group relation plus server moderation.</summary>
public sealed record GroupViewer(ulong UserId, bool CanModerate, bool CanConfigure);

public sealed record GroupsPage(
    ulong GuildId,
    string GuildName,
    GroupViewer Viewer,
    bool CanCreate,
    GroupDirectoryFilter Filter,
    string? Search,
    GroupDirectory Directory,
    MemberNames Names,
    BannerModel? Banner)
{
    public string Url(GroupDirectoryFilter? filter = null, int? page = null, string? search = null)
    {
        var query = new List<string>();
        var chosen = filter ?? Filter;
        if (chosen != GroupDirectoryFilter.Mine)
            query.Add("filter=" + chosen.ToString().ToLowerInvariant());
        string? q = search ?? Search;
        if (!string.IsNullOrWhiteSpace(q))
            query.Add("q=" + Uri.EscapeDataString(q));
        int p = page ?? 1;
        if (p > 1)
            query.Add("page=" + p.ToString(CultureInfo.InvariantCulture));
        return $"/groups/{GuildId}" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }
}

public sealed record GroupFormState(string Section, string? Error, string? Name = null, string? Steam = null,
    string? Description = null, string? Member = null);

public sealed record GroupPage(
    ulong GuildId,
    string GuildName,
    GroupViewer Viewer,
    GroupDetail Detail,
    MemberNames Names,
    MemberProfile[] InviteCandidates,
    BannerModel? Banner,
    string BackUrl,
    GroupFormState? Form,
    GroupAvailabilityBoard? Board = null,
    bool Evening = false,
    string? Tab = null)
{
    /// <summary>Confirmed events of the members on the board date.</summary>
    public ILookup<ulong, BoardEvent> BoardEvents { get; init; } = HappyMix.Bot.Web.BoardEvents.Empty;

    /// <summary>Upcoming dates of this group's events; empty for people who can't see the roster.</summary>
    public IReadOnlyList<EventRow> Events { get; init; } = [];

    /// <summary>Availability is the default Members tab when the board is visible; invite errors open the roster.</summary>
    public bool ShowRoster => Board is null || Tab == "roster" || Form?.Section == "members";

    public int FirstHour => Evening ? 12 : 0;
    public int HourSpan => Evening ? 12 : 24;

    public string DayUrl(DateOnly day) => Link(day, Evening, null) + "#availability";
    public string HoursUrl(bool evening) => Link(Board?.Date, evening, null) + "#availability";
    public string TabUrl(string tab) => Link(Board?.Date, Evening, tab == "roster" ? "roster" : null) + "#members";

    private string Link(DateOnly? day, bool evening, string? tab)
    {
        var query = new List<string>();
        if (day is { } date && Board is { } board && date != board.Today)
            query.Add("day=" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (evening)
            query.Add("hours=12-24");
        if (tab is not null)
            query.Add("tab=" + tab);
        return Url + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }

    /// <summary>Left and width as percentages of the visible hours, or null when the range is outside them.</summary>
    public (double Left, double Width)? Place(BoardRange range)
    {
        double start = Math.Max(GroupAvailabilityBoard.StartHours(range), FirstHour);
        double end = Math.Min(GroupAvailabilityBoard.EndHours(range), FirstHour + HourSpan);
        return end <= start ? null : ((start - FirstHour) / HourSpan * 100, (end - start) / HourSpan * 100);
    }

    /// <summary>The longest run of visible hours with the most members free (at least two), or null.</summary>
    public (int From, int To, int Count)? BestOverlap()
    {
        if (Board is null)
            return null;
        var hours = Enumerable.Range(FirstHour, HourSpan).ToArray();
        int best = hours.Max(hour => Board.FreeByHour[hour]);
        if (best < 2)
            return null;
        (int From, int To)? run = null, current = null;
        foreach (int hour in hours)
        {
            if (Board.FreeByHour[hour] == best)
                current = current is { } open ? (open.From, hour + 1) : (hour, hour + 1);
            else
                current = null;
            if (current is { } candidate && (run is null || candidate.To - candidate.From > run.Value.To - run.Value.From))
                run = candidate;
        }
        return (run!.Value.From, run.Value.To, best);
    }

    public GroupSummary Group => Detail.Summary;
    public string Url => $"/groups/{GuildId}/{Group.Id}";
    public bool IsMember => Group.Relation is GroupRelation.Member or GroupRelation.Organizer;
    public string? ErrorFor(string section) => Form?.Section == section ? Form.Error : null;
}

public sealed record CreateGroupPage(ulong GuildId, string GuildName, GroupViewer Viewer, bool CanCreate,
    string? Name, string? Description, string[] GameNames, string[] GameSteams, string? Error, string? ErrorField)
{
    public const int GameRows = 3;
    public string? GameName(int index) => index < GameNames.Length ? GameNames[index] : null;
    public string? GameSteam(int index) => index < GameSteams.Length ? GameSteams[index] : null;
}

/// <summary>Roster display names with a safe fallback. Never produces a Discord mention.</summary>
public sealed class MemberNames(IReadOnlyDictionary<ulong, MemberProfile> members, bool available)
{
    public bool Available => available;

    public MemberProfile Get(ulong id) => members.TryGetValue(id, out var profile) ? profile : MemberProfile.Missing(id);

    /// <summary>"Former member · …4821" for someone who left; "Member · …4821" when Discord didn't return the list.</summary>
    public string Name(ulong id, Func<string, string> localize)
    {
        var profile = Get(id);
        if (profile.InServer)
            return profile.DisplayName;
        string suffix = "…" + id.ToString(CultureInfo.InvariantCulture)[^Math.Min(4, id.ToString(CultureInfo.InvariantCulture).Length)..];
        return $"{localize(available ? "Former member" : "Member")} · {suffix}";
    }
}

/// <summary>A destructive action behind a disclosure that names the group or member affected.</summary>
public sealed record ConfirmModel(string Trigger, string Question, string Action, string ConfirmLabel,
    string? Icon = "trash", bool Danger = true, bool Inline = false, string? TriggerLabel = null,
    IReadOnlyDictionary<string, string>? Fields = null, string? DismissLabel = null);

public sealed record AvatarModel(MemberProfile Profile, string Name, bool Small = false);
