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
    GroupFormState? Form)
{
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
    string? Icon = "trash", bool Danger = true, bool Inline = false, string? TriggerLabel = null);

public sealed record AvatarModel(MemberProfile Profile, string Name, bool Small = false);
