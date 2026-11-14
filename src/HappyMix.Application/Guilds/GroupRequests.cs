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

internal sealed class GroupQueries(IGuildGroupStore groups, IAccountLinkStore accountLinks) :
    IQueryHandler<GetVisibleGroupsQuery, GuildGroup[]>, IQueryHandler<SuggestGroupNamesQuery, string[]>
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

    public async Task<Result<string[]>> Handle(SuggestGroupNamesQuery query, CancellationToken cancellationToken)
    {
        var actor = query.Actor;
        var links = await accountLinks.LoadAsync(actor.GuildId);
        var saved = await groups.ListAsync(actor.GuildId);
        return Result.Success(saved.Where(group => group.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .Where(group => query.Action switch
            {
                "join" or "decline" => group.Invites.Contains(actor.UserId),
                "leave" => !links.Same(group.OrganizerId, actor.UserId) && links.Includes(group.Members, actor.UserId),
                _ => actor.CanModerate || links.Same(group.OrganizerId, actor.UserId)
            }).Take(25).Select(group => group.Name).ToArray());
    }
}
