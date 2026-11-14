using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

internal sealed class GroupCommands(IGuildGroupStore groups, IGuildSettingsStore settings, GroupRoles roles) :
    ICommandHandler<CreateGroupCommand, GroupChange>, ICommandHandler<InviteGroupMemberCommand, GroupChange>,
    ICommandHandler<ResolveGroupInviteCommand, GroupChange>, ICommandHandler<LeaveGroupCommand, GroupChange>,
    ICommandHandler<RemoveGroupMemberCommand, GroupChange>, ICommandHandler<DeleteGroupCommand, GroupChange>,
    ICommandHandler<EditGroupDescriptionCommand, GroupChange>, ICommandHandler<RepairGroupRolesCommand, GroupChange>
{
    public Task<Result<GroupChange>> Handle(CreateGroupCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var actor = command.Actor;
            var saved = await settings.GetAsync(actor.GuildId, cancellationToken);
            if (!actor.CanModerate && saved?.Preferences?.OrganizerRoleIds is { Length: > 0 } allowed &&
                !command.RoleIds.Any(allowed.Contains))
                throw new GroupRuleException("You need a configured organizer role to create a group.");

            string name = ValidateName(command.Name);
            string? description = NormalizeDescription(command.Description);
            return await UpdateAsync(actor, name, group => group is null
                ? new GuildGroup(name, actor.UserId, [actor.UserId], [], Description: description)
                : throw new GroupRuleException("A group with that name already exists."), actor.UserId);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(InviteGroupMemberCommand command, CancellationToken cancellationToken) =>
        RunAsync(() => UpdateAsync(command.Actor, command.Name, existing =>
        {
            var group = RequireManaged(existing, command.Actor);
            if (group.Members.Contains(command.MemberId))
                throw new GroupRuleException("That member already belongs to the group.");

            Dictionary<ulong, Guid> tokens = group.InviteTokens is { } saved ? new(saved) : [];
            tokens[command.MemberId] = command.Token;
            return group with { Invites = group.Invites.Append(command.MemberId).Distinct().ToArray(), InviteTokens = tokens };
        }), cancellationToken);

    public Task<Result<GroupChange>> Handle(ResolveGroupInviteCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var actor = command.Actor;
            const string expired = "This invitation is no longer pending.";
            string name = command.Name ?? (await groups.ListAsync(actor.GuildId))
                .FirstOrDefault(group => command.Token is { } token && group.InviteTokens?.GetValueOrDefault(actor.UserId) == token)?.Name
                ?? throw new GroupRuleException(expired);

            return await UpdateAsync(actor, name, existing =>
            {
                if (command.Token is { } token && existing?.InviteTokens?.GetValueOrDefault(actor.UserId) != token)
                    throw new GroupRuleException(expired);

                return GroupInvitation.Resolve(RequireGroup(existing), actor.UserId, command.Accept)
                    ?? throw new GroupRuleException("You have no pending invitation to this group.");
            }, command.Accept ? actor.UserId : null);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(LeaveGroupCommand command, CancellationToken cancellationToken) =>
        RunAsync(() => UpdateAsync(command.Actor, command.Name, existing =>
        {
            var group = RequireGroup(existing);
            ulong userId = command.Actor.UserId;
            if (group.OrganizerId == userId)
                throw new GroupRuleException("You organize this group. Use /group delete to close it.");
            if (!group.Members.Contains(userId))
                throw new GroupRuleException("You are not a member of this group.");
            return group with { Members = group.Members.Where(id => id != userId).ToArray() };
        }, command.Actor.UserId), cancellationToken);

    public Task<Result<GroupChange>> Handle(RemoveGroupMemberCommand command, CancellationToken cancellationToken) =>
        RunAsync(() => UpdateAsync(command.Actor, command.Name, existing =>
        {
            var group = RequireManaged(existing, command.Actor);
            if (group.OrganizerId == command.MemberId)
                throw new GroupRuleException("The organizer owns this group. Use /group delete to close it.");
            return group with
            {
                Members = group.Members.Where(id => id != command.MemberId).ToArray(),
                Invites = group.Invites.Where(id => id != command.MemberId).ToArray(),
                InviteTokens = GroupInvitation.RemoveToken(group, command.MemberId)
            };
        }, command.MemberId), cancellationToken);

    public Task<Result<GroupChange>> Handle(DeleteGroupCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            string name = ValidateName(command.Name);
            string key = GuildGroup.GetNameKey(name);
            var group = RequireManaged((await groups.ListAsync(command.Actor.GuildId))
                .FirstOrDefault(item => GuildGroup.GetNameKey(item.Name) == key), command.Actor);
            await roles.DeleteAsync(command.Actor.GuildId, group);
            return await UpdateAsync(command.Actor, name, _ => null);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(EditGroupDescriptionCommand command, CancellationToken cancellationToken) =>
        RunAsync(() => UpdateAsync(command.Actor, command.Name, existing =>
            RequireManaged(existing, command.Actor) with { Description = NormalizeDescription(command.Description) }), cancellationToken);

    public Task<Result<GroupChange>> Handle(RepairGroupRolesCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            if (!command.Actor.CanModerate)
                throw new GroupRuleException("Only server moderators can repair group roles.");

            try
            {
                await roles.SyncAsync(command.Actor.GuildId, command.MemberId);
            }
            catch (GroupRoleException exception) when (command.MemberId is null)
            {
                throw new GroupRoleException($"{exception.Message} Check the Server Members Intent in the Developer Portal.", exception);
            }
            return new GroupChange(null);
        }, cancellationToken);

    private async Task<Result<GroupChange>> RunAsync(Func<Task<GroupChange>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return Result.Success(await roles.RunAsync(operation, cancellationToken));
        }
        catch (GroupRuleException exception)
        {
            return Result.Failure<GroupChange>(new("Group.Rule", exception.Message));
        }
        catch (GroupRoleException exception)
        {
            return Result.Failure<GroupChange>(new("Group.Role", exception.Message));
        }
    }

    private async Task<GroupChange> UpdateAsync(GroupActor actor, string input, Func<GuildGroup?, GuildGroup?> update,
        ulong? syncUser = null)
    {
        string name = ValidateName(input);
        await groups.UpdateAsync(actor.GuildId, name, update);
        if (syncUser is not null)
        {
            try
            {
                await roles.SyncAsync(actor.GuildId, syncUser, name);
            }
            catch (GroupRoleException)
            {
                return new GroupChange(name, RoleSyncFailed: true);
            }
        }

        return new GroupChange(name);
    }

    private static GuildGroup RequireGroup(GuildGroup? group) =>
        group ?? throw new GroupRuleException("That group does not exist in this server.");

    private static GuildGroup RequireManaged(GuildGroup? existing, GroupActor actor)
    {
        var group = RequireGroup(existing);
        if (group.OrganizerId != actor.UserId && !actor.CanModerate)
            throw new GroupRuleException("Only the group's organizer or a server moderator can manage it.");
        return group;
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40 || name.Any(character =>
            !char.IsLetterOrDigit(character) && character is not (' ' or '-' or '_')))
            throw new GroupRuleException("Use a group name of 1 to 40 letters, digits, spaces, hyphens or underscores.");
        return name;
    }

    private static string? NormalizeDescription(string? description)
    {
        description = description?.Trim();
        if (description?.Length > 200)
            throw new GroupRuleException("Use a description of up to 200 characters.");
        return string.IsNullOrEmpty(description) ? null : description;
    }

    private sealed class GroupRuleException(string message) : Exception(message);
}
