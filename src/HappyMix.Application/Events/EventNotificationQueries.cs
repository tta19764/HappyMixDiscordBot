using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Events;

public sealed record CanManageNotificationsQuery(ulong GuildId, ulong UserId, bool Administrator, string Scope, Guid Target) : IQuery<bool>;
public sealed record VoiceParticipantsQuery(ulong GuildId, ulong[] ConnectedAccounts) : IQuery<ulong[]>;

/// <summary>The group or event a notification route belongs to: its name, and for an event its group.</summary>
public sealed record NotificationTarget(string Scope, Guid Id, string Title, Guid? GroupId, ulong OrganizerId);

/// <summary>A group or event by id, or null when it doesn't exist in the server.</summary>
public sealed record GetNotificationTargetQuery(ulong GuildId, string Scope, Guid Target) : IQuery<NotificationTarget?>;

/// <summary>
/// Groups and events whose channel notifications <paramref name="UserId"/> may manage, for command autocomplete:
/// the ones they organize, or every group when they are an administrator. Upcoming events only.
/// </summary>
public sealed record ListManageableNotificationTargetsQuery(ulong GuildId, ulong UserId, bool Administrator, DateTimeOffset Now)
    : IQuery<IReadOnlyList<NotificationTarget>>;

internal sealed class EventNotificationQueries(IEventStore events, IGuildGroupStore groups, IAccountLinkStore links)
    : IQueryHandler<CanManageNotificationsQuery, bool>, IQueryHandler<VoiceParticipantsQuery, ulong[]>,
      IQueryHandler<GetNotificationTargetQuery, NotificationTarget?>,
      IQueryHandler<ListManageableNotificationTargetsQuery, IReadOnlyList<NotificationTarget>>
{
    public async Task<Result<bool>> Handle(CanManageNotificationsQuery request, CancellationToken cancellationToken)
    {
        ulong primary = await PrimaryAsync(request.GuildId, request.UserId);
        var target = await FindAsync(request.GuildId, request.Scope, request.Target);
        return target is not null && (target.OrganizerId == primary || request.Administrator);
    }

    public async Task<Result<ulong[]>> Handle(VoiceParticipantsQuery request, CancellationToken cancellationToken)
    {
        var connected = request.ConnectedAccounts.ToHashSet();
        foreach (var account in await links.ListAsync(request.GuildId))
            if (connected.Contains(account.AlternateId)) connected.Add(account.PrimaryId);
        return connected.ToArray();
    }

    public async Task<Result<NotificationTarget?>> Handle(GetNotificationTargetQuery request, CancellationToken cancellationToken) =>
        await FindAsync(request.GuildId, request.Scope, request.Target);

    public async Task<Result<IReadOnlyList<NotificationTarget>>> Handle(ListManageableNotificationTargetsQuery request,
        CancellationToken cancellationToken)
    {
        ulong primary = await PrimaryAsync(request.GuildId, request.UserId);
        var groupTargets = (await groups.ListAsync(request.GuildId))
            .Where(group => request.Administrator || group.OrganizerId == primary)
            .Select(group => new NotificationTarget("group", group.Id, group.Name, null, group.OrganizerId));
        var eventTargets = (await events.ListForUserAsync(request.GuildId, primary, request.Now))
            .Where(details => details.Event.CanceledAt is null && (request.Administrator || details.Event.OrganizerId == primary))
            .Select(details => new NotificationTarget("event", details.Event.Id, details.Event.Title, details.Event.GroupId, details.Event.OrganizerId));
        return Result.Success<IReadOnlyList<NotificationTarget>>(groupTargets.Concat(eventTargets).ToArray());
    }

    private async Task<ulong> PrimaryAsync(ulong guildId, ulong userId) =>
        (await links.ListAsync(guildId)).FirstOrDefault(link => link.AlternateId == userId)?.PrimaryId ?? userId;

    private async Task<NotificationTarget?> FindAsync(ulong guildId, string scope, Guid target)
    {
        if (scope == "event")
            return (await events.GetAsync(guildId, target)) is { } details
                ? new NotificationTarget("event", target, details.Event.Title, details.Event.GroupId, details.Event.OrganizerId)
                : null;
        if (scope == "group")
            return (await groups.ListAsync(guildId)).FirstOrDefault(group => group.Id == target) is { } group
                ? new NotificationTarget("group", target, group.Name, null, group.OrganizerId)
                : null;
        return null;
    }
}
