using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public sealed record GroupActor(ulong GuildId, ulong UserId, bool CanModerate);
public abstract record GroupCommand(GroupActor Actor) : ICommand<GroupChange>;
public sealed record CreateGroupCommand(GroupActor Actor, string Name, string? Description, ulong[] RoleIds,
    GroupGameInput[]? Games = null) : GroupCommand(Actor);
public sealed record InviteGroupMemberCommand(GroupActor Actor, string Name, ulong MemberId, Guid Token) : GroupCommand(Actor);
public sealed record ResolveGroupInviteCommand(GroupActor Actor, string? Name, Guid? Token, bool Accept) : GroupCommand(Actor);
public sealed record LeaveGroupCommand(GroupActor Actor, string Name) : GroupCommand(Actor);
public sealed record RemoveGroupMemberCommand(GroupActor Actor, string Name, ulong MemberId) : GroupCommand(Actor);
public sealed record DeleteGroupCommand(GroupActor Actor, string Name) : GroupCommand(Actor);
public sealed record EditGroupDescriptionCommand(GroupActor Actor, string Name, string? Description) : GroupCommand(Actor);
public sealed record RepairGroupRolesCommand(GroupActor Actor, ulong? MemberId) : GroupCommand(Actor);
public sealed record GroupChange(string? Name, bool RoleSyncFailed = false, Guid? GroupId = null);

public sealed record GetVisibleGroupsQuery(GroupActor Actor, bool MembersOnly) : IQuery<GuildGroup[]>;
public sealed record SuggestGroupNamesQuery(GroupActor Actor, string Action, string Search) : IQuery<string[]>;

/// <summary>The id of a group the person can see (open groups included), by its name.</summary>
public sealed record FindGroupQuery(GroupActor Actor, string Name) : IQuery<Guid>;

internal sealed class GroupQueries(IGuildGroupStore groups, IAccountLinkStore accountLinks) :
    IQueryHandler<GetVisibleGroupsQuery, GuildGroup[]>, IQueryHandler<SuggestGroupNamesQuery, string[]>, IQueryHandler<FindGroupQuery, Guid>
{
    public async Task<Result<GuildGroup[]>> Handle(GetVisibleGroupsQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var links = await accountLinks.LoadAsync(actor.GuildId);
        var saved = await groups.ListAsync(actor.GuildId);
        return Result.Success(saved.Where(group => links.Includes(group.Members, actor.UserId) ||
                (!query.MembersOnly && (actor.CanModerate || group.Invites.Contains(actor.UserId))))
            .Select(group => group with { Members = links.Representatives(group.Members), Invites = group.Invites })
            .ToArray());
    }

    public async Task<Result<Guid>> Handle(FindGroupQuery query, CancellationToken cancellationToken)
    {
        var links = await accountLinks.LoadAsync(query.Actor.GuildId);
        string name = (query.Name ?? "").Trim();
        var group = (await groups.ListAsync(query.Actor.GuildId))
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) && GroupWorkspaceQueries.CanSee(item, query.Actor, links));
        return group is null ? Result.Failure<Guid>(new ErrorDescriptor("Groups.NotFound", GroupWorkspaceQueries.NotFound)) : group.Id;
    }

    public async Task<Result<string[]>> Handle(SuggestGroupNamesQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var links = await accountLinks.LoadAsync(actor.GuildId);
        var saved = await groups.ListAsync(actor.GuildId);
        return Result.Success(saved.Where(group => group.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .Where(group => query.Action switch
            {
                "join" or "decline" => group.Invites.Contains(actor.UserId),
                // Open groups the person isn't in and hasn't asked to join yet; then the ones they did ask.
                "request-join" => group.Open && !links.Includes(group.Members, actor.UserId) && !links.Same(group.OrganizerId, actor.UserId)
                    && !(group.JoinRequests ?? []).Any(id => links.Same(id, actor.UserId)),
                "withdraw-request" => (group.JoinRequests ?? []).Any(id => links.Same(id, actor.UserId)),
                // Groups the person belongs to: suggesting games and following the watchlist.
                "suggest-game" or "watch-show" or "watch-next" or "watch-finish" => links.Includes(group.Members, actor.UserId) || links.Same(group.OrganizerId, actor.UserId),
                "leave" => !links.Same(group.OrganizerId, actor.UserId) && links.Includes(group.Members, actor.UserId),
                _ => actor.CanModerate || links.Same(group.OrganizerId, actor.UserId)
            }).Take(25).Select(group => group.Name).ToArray());
    }
}
