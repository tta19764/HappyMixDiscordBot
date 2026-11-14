using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>
/// Group use cases. Every rule compares members, not accounts: linked alternates act as their family's member.
/// Moderation (<see cref="GroupActor.CanModerate"/>) is always the requesting account's own Discord permission.
/// </summary>
internal sealed partial class GroupCommands(IGuildGroupStore groups, IGuildSettingsStore settings, GroupRoles roles,
    IAccountLinkStore accountLinks) :
    ICommandHandler<CreateGroupCommand, GroupChange>, ICommandHandler<InviteGroupMemberCommand, GroupChange>,
    ICommandHandler<ResolveGroupInviteCommand, GroupChange>, ICommandHandler<LeaveGroupCommand, GroupChange>,
    ICommandHandler<RemoveGroupMemberCommand, GroupChange>, ICommandHandler<DeleteGroupCommand, GroupChange>,
    ICommandHandler<EditGroupDescriptionCommand, GroupChange>, ICommandHandler<RepairGroupRolesCommand, GroupChange>,
    ICommandHandler<AddGroupGameCommand, GroupChange>, ICommandHandler<UpdateGroupGameCommand, GroupChange>,
    ICommandHandler<RemoveGroupGameCommand, GroupChange>
{
    public Task<Result<GroupChange>> Handle(CreateGroupCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var actor = command.Actor;
            var links = await accountLinks.LoadAsync(actor.GuildId);
            var saved = await settings.GetAsync(actor.GuildId, cancellationToken);
            if (!actor.CanModerate && saved?.Preferences?.OrganizerRoleIds is { Length: > 0 } allowed &&
                !command.RoleIds.Any(allowed.Contains))
                throw new GroupRuleException("You need a configured organizer role to create a group.");

            string name = ValidateName(command.Name);
            string? description = NormalizeDescription(command.Description);
            var games = (command.Games ?? []).Select(game => NormalizeGame(game.Name, game.Steam)).ToArray();
            if (games.Length > MaxGames)
                throw new GroupRuleException(TooManyGames);
            if (games.Where(game => game.SteamAppId is not null).GroupBy(game => game.SteamAppId).Any(group => group.Count() > 1))
                throw new GroupRuleException(DuplicateGame);

            ulong member = actor.UserId;
            var change = await UpdateAsync(actor, name, group => group is null
                ? new GuildGroup(name, member, [member], [], Description: description)
                : throw new GroupRuleException("A group with that name already exists."), links.Accounts(member));
            string key = GuildGroup.GetNameKey(name);
            Guid id = (await groups.ListAsync(actor.GuildId)).First(group => GuildGroup.GetNameKey(group.Name) == key).Id;
            foreach (var (gameName, steam) in games.Select(game => (game.Name, game.SteamAppId)))
                await groups.SaveGameAsync(actor.GuildId, CreateGame(id, Guid.NewGuid(), gameName, steam));
            return change with { GroupId = id };
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(InviteGroupMemberCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var links = await accountLinks.LoadAsync(command.Actor.GuildId);
            ulong invitee = command.MemberId;
            return await UpdateAsync(command.Actor, command.Name, existing =>
            {
                var group = RequireManaged(existing, command.Actor, links);
                if (links.Includes(group.Members, invitee))
                    throw new GroupRuleException("That member already belongs to the group.");

                // One pending invitation per family, addressed to the selected account.
                var family = links.Accounts(invitee);
                var tokens = GroupInvitation.RemoveTokens(group, family) ?? [];
                tokens[invitee] = command.Token;
                return group with
                {
                    Invites = group.Invites.Where(id => !family.Contains(id)).Append(invitee).ToArray(),
                    InviteTokens = tokens
                };
            });
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(ResolveGroupInviteCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var actor = command.Actor;
            var links = await accountLinks.LoadAsync(actor.GuildId);
            var family = links.Accounts(actor.UserId);
            const string expired = "This invitation is no longer pending.";
            bool TokenMatches(GuildGroup? group) => command.Token is { } token &&
                group?.InviteTokens is { } tokens && tokens.GetValueOrDefault(actor.UserId) == token;

            string name = command.Name ?? (await groups.ListAsync(actor.GuildId)).FirstOrDefault(TokenMatches)?.Name
                ?? throw new GroupRuleException(expired);

            return await UpdateAsync(actor, name, existing =>
            {
                if (command.Token is not null && !TokenMatches(existing))
                    throw new GroupRuleException(expired);

                return GroupInvitation.Resolve(RequireGroup(existing), family, actor.UserId, command.Accept)
                    ?? throw new GroupRuleException("You have no pending invitation to this group.");
            }, command.Accept ? family : null);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(LeaveGroupCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var links = await accountLinks.LoadAsync(command.Actor.GuildId);
            var family = links.Accounts(command.Actor.UserId);
            return await UpdateAsync(command.Actor, command.Name, existing =>
            {
                var group = RequireGroup(existing);
                if (links.Same(group.OrganizerId, command.Actor.UserId))
                    throw new GroupRuleException("You organize this group. Use /group delete to close it.");
                if (!links.Includes(group.Members, command.Actor.UserId))
                    throw new GroupRuleException("You are not a member of this group.");
                // The whole family leaves once, whichever linked account asked.
                return group with { Members = group.Members.Where(id => !family.Contains(id)).ToArray() };
            }, family);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(RemoveGroupMemberCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var links = await accountLinks.LoadAsync(command.Actor.GuildId);
            var family = links.Accounts(command.MemberId);
            return await UpdateAsync(command.Actor, command.Name, existing =>
            {
                var group = RequireManaged(existing, command.Actor, links);
                if (links.Same(group.OrganizerId, command.MemberId))
                    throw new GroupRuleException("The organizer owns this group. Use /group delete to close it.");
                return group with
                {
                    Members = group.Members.Where(id => !family.Contains(id)).ToArray(),
                    Invites = group.Invites.Where(id => !family.Contains(id)).ToArray(),
                    InviteTokens = GroupInvitation.RemoveTokens(group, family) is { Count: > 0 } left ? left : null
                };
            }, family);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(DeleteGroupCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var links = await accountLinks.LoadAsync(command.Actor.GuildId);
            string name = ValidateName(command.Name);
            string key = GuildGroup.GetNameKey(name);
            var group = RequireManaged((await groups.ListAsync(command.Actor.GuildId))
                .FirstOrDefault(item => GuildGroup.GetNameKey(item.Name) == key), command.Actor, links);
            await roles.DeleteAsync(command.Actor.GuildId, group);
            return await UpdateAsync(command.Actor, name, _ => null);
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(EditGroupDescriptionCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var links = await accountLinks.LoadAsync(command.Actor.GuildId);
            return await UpdateAsync(command.Actor, command.Name, existing =>
                RequireManaged(existing, command.Actor, links) with { Description = NormalizeDescription(command.Description) });
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(RepairGroupRolesCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            // The requesting account's own Discord permission; a linked moderator account never lends it.
            if (!command.Actor.CanModerate)
                throw new GroupRuleException("Only server moderators can repair group roles.");

            try
            {
                if (command.MemberId is { } member)
                {
                    var links = await accountLinks.LoadAsync(command.Actor.GuildId);
                    foreach (ulong account in links.Accounts(member))
                        await roles.SyncAsync(command.Actor.GuildId, account);
                }
                else
                {
                    await roles.SyncAsync(command.Actor.GuildId);
                }
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

    /// <summary>Saves the group, then reconciles the Discord role for each listed account.</summary>
    private async Task<GroupChange> UpdateAsync(GroupActor actor, string input, Func<GuildGroup?, GuildGroup?> update,
        IReadOnlyCollection<ulong>? syncAccounts = null)
    {
        string name = ValidateName(input);
        await groups.UpdateAsync(actor.GuildId, name, update);
        if (syncAccounts is { Count: > 0 })
        {
            try
            {
                foreach (ulong account in syncAccounts)
                    await roles.SyncAsync(actor.GuildId, account, name);
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

    private static GuildGroup RequireManaged(GuildGroup? existing, GroupActor actor, MemberLinks links)
    {
        var group = RequireGroup(existing);
        if (!links.Same(group.OrganizerId, actor.UserId) && !actor.CanModerate)
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
