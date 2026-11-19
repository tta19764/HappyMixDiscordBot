using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

/// <summary>Makes a group open (anyone can ask to join) or closed (invitation only). Closing drops pending requests.</summary>
public sealed record SetGroupOpenCommand(GroupActor Actor, Guid GroupId, bool Open) : GroupCommand(Actor);

/// <summary>Asks to join an open group. Someone already invited joins straight away.</summary>
public sealed record RequestToJoinGroupCommand(GroupActor Actor, Guid GroupId) : GroupCommand(Actor);

public sealed record CancelJoinRequestCommand(GroupActor Actor, Guid GroupId) : GroupCommand(Actor);

/// <summary>The organizer (or a moderator) lets <paramref name="MemberId"/> in, or turns the request down.</summary>
public sealed record ResolveJoinRequestCommand(GroupActor Actor, Guid GroupId, ulong MemberId, bool Approve) : GroupCommand(Actor);

/// <summary>
/// A member suggests a game for the group. <paramref name="Provider"/> and <paramref name="ExternalId"/> come from a
/// catalog search (steam or igdb); <paramref name="Steam"/> is an optional Steam App ID or store link.
/// </summary>
public sealed record SuggestGroupGameCommand(GroupActor Actor, Guid GroupId, string Name, string? Steam, string? Provider = null,
    string? ExternalId = null) : GroupCommand(Actor);

/// <summary>The organizer adds a suggested game to the group, or dismisses it.</summary>
public sealed record ResolveGameSuggestionCommand(GroupActor Actor, Guid GroupId, Guid SuggestionId, bool Accept) : GroupCommand(Actor);

/// <summary>Open groups, join requests and game suggestions. They share the group command gate and rules.</summary>
internal sealed partial class GroupCommands
{
    private const int MaxSuggestions = 20;
    private const int MaxJoinRequests = 50;

    public Task<Result<GroupChange>> Handle(SetGroupOpenCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var group = await RequireManagedByIdAsync(command.Actor, command.GroupId);
            return await UpdateAsync(command.Actor, group.Name, existing => RequireGroup(existing) with
            {
                Open = command.Open,
                JoinRequests = command.Open ? existing!.JoinRequests : null
            }) with { GroupId = group.Id };
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(RequestToJoinGroupCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var actor = command.Actor;
            var links = await accountLinks.LoadAsync(actor.GuildId);
            var family = links.Accounts(actor.UserId);
            var group = await VisibleByIdAsync(actor, command.GroupId, links);
            if (links.Includes(group.Members, actor.UserId))
                throw new GroupRuleException("You already belong to this group.");

            // An invitation already counts as the organizer's yes.
            if (group.Invites.Any(family.Contains))
            {
                var joined = await UpdateAsync(actor, group.Name, existing =>
                    GroupInvitation.Resolve(RequireGroup(existing), family, actor.UserId, true)
                    ?? throw new GroupRuleException("You have no pending invitation to this group."), family);
                return joined with { GroupId = group.Id };
            }
            if (!group.Open)
                throw new GroupRuleException("This group is invitation only. Ask its organizer to invite you.");
            var change = await UpdateAsync(actor, group.Name, existing =>
            {
                var current = RequireGroup(existing);
                var requests = (current.JoinRequests ?? []).Where(id => !family.Contains(id)).ToArray();
                if (requests.Length >= MaxJoinRequests)
                    throw new GroupRuleException("This group has too many open requests. Try again later.");
                return current with { JoinRequests = [.. requests, actor.UserId] };
            });
            await NotifyAsync(new GroupNotice(GroupNoticeKind.JoinRequested, actor.GuildId, group.Id, group.Name, group.OrganizerId, actor.UserId),
                cancellationToken);
            return change with { GroupId = group.Id };
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(CancelJoinRequestCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var links = await accountLinks.LoadAsync(command.Actor.GuildId);
            var family = links.Accounts(command.Actor.UserId);
            var group = await VisibleByIdAsync(command.Actor, command.GroupId, links);
            return await UpdateAsync(command.Actor, group.Name, existing => RequireGroup(existing) with
            {
                JoinRequests = (existing!.JoinRequests ?? []).Where(id => !family.Contains(id)).ToArray()
            }) with { GroupId = group.Id };
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(ResolveJoinRequestCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var links = await accountLinks.LoadAsync(command.Actor.GuildId);
            var group = await RequireManagedByIdAsync(command.Actor, command.GroupId);
            var family = links.Accounts(command.MemberId);
            ulong? requester = (group.JoinRequests ?? []).FirstOrDefault(family.Contains) is var found && found != 0 ? found : null;
            if (requester is not { } account)
                throw new GroupRuleException("That request is no longer pending.");

            var change = await UpdateAsync(command.Actor, group.Name, existing =>
            {
                var current = RequireGroup(existing);
                var requests = (current.JoinRequests ?? []).Where(id => !family.Contains(id)).ToArray();
                return command.Approve
                    ? current with
                    {
                        Members = current.Members.Where(id => !family.Contains(id)).Append(account).ToArray(),
                        Invites = current.Invites.Where(id => !family.Contains(id)).ToArray(),
                        InviteTokens = GroupInvitation.RemoveTokens(current, family) is { Count: > 0 } left ? left : null,
                        JoinRequests = requests
                    }
                    : current with { JoinRequests = requests };
            }, command.Approve ? family : null);
            await NotifyAsync(new GroupNotice(command.Approve ? GroupNoticeKind.JoinApproved : GroupNoticeKind.JoinDeclined, command.Actor.GuildId,
                group.Id, group.Name, account, command.Actor.UserId), cancellationToken);
            return change with { GroupId = group.Id };
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(SuggestGroupGameCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var actor = command.Actor;
            var links = await accountLinks.LoadAsync(actor.GuildId);
            var group = await VisibleByIdAsync(actor, command.GroupId, links);
            if (!GroupWorkspaceQueries.CanViewRoster(group, actor, links))
                throw new GroupRuleException("Only members can suggest games for this group.");
            var (provider, externalId) = Catalog(command.Provider, command.ExternalId);
            var (name, steamAppId) = NormalizeGame(command.Name, provider is null ? command.Steam : null);
            if (steamAppId is not null)
                (provider, externalId) = (GroupGame.SteamProvider, steamAppId);

            var games = (await groups.ListGamesAsync(actor.GuildId)).Where(item => item.GroupId == group.Id).ToArray();
            bool Same(string otherName, string? otherProvider, string? otherId) =>
                string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase) ||
                provider is not null && otherProvider == provider && otherId == externalId;
            if (games.Any(game => Same(game.Name, game.Provider, game.ExternalId)))
                throw new GroupRuleException("The group already has that game.");

            ulong member = links.Member(actor.UserId);
            var suggestion = new GameSuggestion(Guid.NewGuid(), name, provider, externalId, member, DateTimeOffset.UtcNow);
            var change = await UpdateAsync(actor, group.Name, existing =>
            {
                var current = RequireGroup(existing);
                var pending = current.Suggestions ?? [];
                if (pending.Any(item => Same(item.Name, item.Provider, item.ExternalId)))
                    throw new GroupRuleException("Someone already suggested that game.");
                if (pending.Length >= MaxSuggestions)
                    throw new GroupRuleException("This group has too many suggestions waiting. Try again once the organizer has looked at them.");
                return current with { Suggestions = [.. pending, suggestion] };
            });
            if (!links.Same(group.OrganizerId, actor.UserId))
                await NotifyAsync(new GroupNotice(GroupNoticeKind.GameSuggested, actor.GuildId, group.Id, group.Name, group.OrganizerId, actor.UserId, name),
                    cancellationToken);
            return change with { GroupId = group.Id };
        }, cancellationToken);

    public Task<Result<GroupChange>> Handle(ResolveGameSuggestionCommand command, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var group = await RequireManagedByIdAsync(command.Actor, command.GroupId);
            var suggestion = (group.Suggestions ?? []).FirstOrDefault(item => item.Id == command.SuggestionId)
                ?? throw new GroupRuleException("That suggestion is no longer waiting.");
            if (command.Accept)
            {
                var games = (await groups.ListGamesAsync(command.Actor.GuildId)).Where(item => item.GroupId == group.Id).ToArray();
                if (games.Length >= MaxGames)
                    throw new GroupRuleException(TooManyGames);
                if (!await groups.SaveGameAsync(command.Actor.GuildId,
                        new GroupGame(Guid.NewGuid(), group.Id, suggestion.Name, suggestion.Provider, suggestion.ExternalId)))
                    throw new GroupRuleException(DuplicateGame);
            }
            var change = await UpdateAsync(command.Actor, group.Name, existing => RequireGroup(existing) with
            {
                Suggestions = (existing!.Suggestions ?? []).Where(item => item.Id != command.SuggestionId).ToArray()
            });
            if (command.Accept && suggestion.SuggestedBy != command.Actor.UserId)
                await NotifyAsync(new GroupNotice(GroupNoticeKind.SuggestionAccepted, command.Actor.GuildId, group.Id, group.Name,
                    suggestion.SuggestedBy, command.Actor.UserId, suggestion.Name), cancellationToken);
            return change with { GroupId = group.Id };
        }, cancellationToken);

    /// <summary>A group the actor may see (open groups count), or "not found".</summary>
    private async Task<GuildGroup> VisibleByIdAsync(GroupActor actor, Guid groupId, MemberLinks links)
    {
        var group = (await groups.ListAsync(actor.GuildId)).FirstOrDefault(item => item.Id == groupId);
        if (group is null || !GroupWorkspaceQueries.CanSee(group, actor, links))
            throw new GroupRuleException(GroupWorkspaceQueries.NotFound);
        return group;
    }

    /// <summary>A catalog reference from a search: steam app ids or igdb game ids, digits only.</summary>
    private static (string? Provider, string? ExternalId) Catalog(string? provider, string? externalId) =>
        provider is GroupGame.SteamProvider or GroupGame.IgdbProvider && externalId is { Length: > 0 and <= 20 } id && id.All(char.IsAsciiDigit)
            ? (provider, id) : (null, null);

    private async Task NotifyAsync(GroupNotice notice, CancellationToken cancellationToken)
    {
        foreach (var notifier in notifiers)
        {
            try
            {
                await notifier.NotifyAsync(notice, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Best effort: a closed inbox or Discord hiccup never undoes the change.
            }
        }
    }
}
