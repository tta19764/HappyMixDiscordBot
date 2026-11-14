using System.Globalization;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public enum GroupDirectoryFilter
{
    Mine,
    Invited,
    All
}

/// <summary>How the acting user relates to a group. Moderator access is carried separately on the actor.</summary>
public enum GroupRelation
{
    None,
    Invited,
    Member,
    Organizer
}

public sealed record GroupSummary(Guid Id, string Name, string? Description, ulong OrganizerId, int MemberCount,
    string[] Games, GroupRelation Relation, bool CanManage);

public sealed record GroupDirectory(GroupSummary[] Items, int Total, int Page, int PageCount,
    int MineCount, int InvitedCount, int AllCount);

/// <summary>
/// A group as the actor may see it. Invited users see the overview only; the roster and games need membership,
/// organizer rights or server moderation. Pending invitations are visible only to people who can manage them.
/// </summary>
public sealed record GroupDetail(GroupSummary Summary, ulong[] Members, ulong[] Invites, GroupGame[] Games,
    bool CanViewRoster, bool CanLeave);

public sealed record GroupGameInput(string Name, string? Steam);

public sealed record GetGroupDirectoryQuery(GroupActor Actor, GroupDirectoryFilter Filter, string? Search, int Page,
    int PageSize = 12) : IQuery<GroupDirectory>;
public sealed record GetGroupDetailQuery(GroupActor Actor, Guid GroupId) : IQuery<GroupDetail>;
public sealed record CanCreateGroupQuery(GroupActor Actor, ulong[] RoleIds) : IQuery<bool>;

public sealed record AddGroupGameCommand(GroupActor Actor, Guid GroupId, string Name, string? Steam) : GroupCommand(Actor);
public sealed record UpdateGroupGameCommand(GroupActor Actor, Guid GroupId, Guid GameId, string Name, string? Steam)
    : GroupCommand(Actor);
public sealed record RemoveGroupGameCommand(GroupActor Actor, Guid GroupId, Guid GameId) : GroupCommand(Actor);

/// <summary>Normalizes Steam App IDs entered as a number or a Steam store/community link.</summary>
public static class SteamApps
{
    public const string InvalidMessage =
        "Enter a Steam App ID like 730 or a Steam store link like https://store.steampowered.com/app/730/.";

    /// <summary>Returns true for empty input (no Steam link) or a valid ID; <paramref name="appId"/> is the canonical number.</summary>
    public static bool TryNormalize(string? input, out string? appId)
    {
        appId = null;
        string value = input?.Trim() ?? "";
        if (value.Length == 0)
            return true;

        if (value.All(char.IsAsciiDigit))
            return TryId(value, out appId);

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            return false;

        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (!host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0].Equals("app", StringComparison.OrdinalIgnoreCase) &&
            segments[1].All(char.IsAsciiDigit) && TryId(segments[1], out appId);
    }

    public static string StoreUrl(string appId) => $"https://store.steampowered.com/app/{appId}/";

    private static bool TryId(string digits, out string? appId)
    {
        appId = uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;
        return appId is not null;
    }
}

internal sealed class GroupWorkspaceQueries(IGuildGroupStore groups, IGuildSettingsStore settings) :
    IQueryHandler<GetGroupDirectoryQuery, GroupDirectory>, IQueryHandler<GetGroupDetailQuery, GroupDetail>,
    IQueryHandler<CanCreateGroupQuery, bool>
{
    internal const string NotFound = "That group doesn't exist or you don't have access to it.";

    public async Task<Result<GroupDirectory>> Handle(GetGroupDirectoryQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var saved = await groups.ListAsync(actor.GuildId);
        var games = (await groups.ListGamesAsync(actor.GuildId)).ToLookup(game => game.GroupId);
        var accessible = saved.Where(group => CanSee(group, actor)).ToArray();
        var mine = accessible.Where(group => group.Members.Contains(actor.UserId)).ToArray();
        var invited = accessible.Where(group => group.Invites.Contains(actor.UserId)).ToArray();
        var source = query.Filter switch
        {
            GroupDirectoryFilter.Mine => mine,
            GroupDirectoryFilter.Invited => invited,
            _ => accessible
        };

        string search = query.Search?.Trim() ?? "";
        var matches = source.Where(group => search.Length == 0 ||
                group.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                group.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true ||
                games[group.Id].Any(game => game.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(group => group.Invites.Contains(actor.UserId) ? 0 : 1)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        int size = Math.Clamp(query.PageSize, 1, 50);
        int pageCount = Math.Max(1, (matches.Length + size - 1) / size);
        int page = Math.Clamp(query.Page, 1, pageCount);
        var items = matches.Skip((page - 1) * size).Take(size)
            .Select(group => Summarize(group, actor, games[group.Id].Select(game => game.Name).ToArray()))
            .ToArray();
        return Result.Success(new GroupDirectory(items, matches.Length, page, pageCount,
            mine.Length, invited.Length, accessible.Length));
    }

    public async Task<Result<GroupDetail>> Handle(GetGroupDetailQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var group = (await groups.ListAsync(actor.GuildId)).FirstOrDefault(item => item.Id == query.GroupId);
        if (group is null || !CanSee(group, actor))
            return Result.Failure<GroupDetail>(new("Group.NotFound", NotFound));

        var games = (await groups.ListGamesAsync(actor.GuildId)).Where(game => game.GroupId == group.Id).ToArray();
        var summary = Summarize(group, actor, games.Select(game => game.Name).ToArray());
        bool roster = summary.CanManage || group.Members.Contains(actor.UserId);
        return Result.Success(new GroupDetail(summary,
            roster ? group.Members : [],
            summary.CanManage ? group.Invites : [],
            roster ? games : [],
            roster,
            group.Members.Contains(actor.UserId) && group.OrganizerId != actor.UserId));
    }

    public async Task<Result<bool>> Handle(CanCreateGroupQuery query, CancellationToken cancellationToken)
    {
        if (query.Actor.CanModerate)
            return Result.Success(true);

        var saved = await settings.GetAsync(query.Actor.GuildId, cancellationToken);
        return Result.Success(saved?.Preferences?.OrganizerRoleIds is not { Length: > 0 } allowed ||
            query.RoleIds.Any(allowed.Contains));
    }

    /// <summary>Groups stay invite-only: members and invitees see their own groups; moderators see the whole server.</summary>
    internal static bool CanSee(GuildGroup group, GroupActor actor) =>
        actor.CanModerate || group.Members.Contains(actor.UserId) || group.Invites.Contains(actor.UserId);

    private static GroupSummary Summarize(GuildGroup group, GroupActor actor, string[] games)
    {
        var relation = group.OrganizerId == actor.UserId ? GroupRelation.Organizer
            : group.Members.Contains(actor.UserId) ? GroupRelation.Member
            : group.Invites.Contains(actor.UserId) ? GroupRelation.Invited
            : GroupRelation.None;
        bool roster = relation is GroupRelation.Organizer or GroupRelation.Member || actor.CanModerate;
        return new GroupSummary(group.Id, group.Name, group.Description, group.OrganizerId, group.Members.Length,
            roster ? games : [], relation, relation == GroupRelation.Organizer || actor.CanModerate);
    }
}
