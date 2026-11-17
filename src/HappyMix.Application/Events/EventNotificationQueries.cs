using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Events;

public sealed record CanManageNotificationsQuery(ulong GuildId, ulong UserId, bool Administrator, string Scope, Guid Target) : IQuery<bool>;
public sealed record VoiceParticipantsQuery(ulong GuildId, ulong[] ConnectedAccounts) : IQuery<ulong[]>;

internal sealed class EventNotificationQueries(IEventStore events, IGuildGroupStore groups, IAccountLinkStore links)
    : IQueryHandler<CanManageNotificationsQuery, bool>, IQueryHandler<VoiceParticipantsQuery, ulong[]>
{
    public async Task<Result<bool>> Handle(CanManageNotificationsQuery request, CancellationToken cancellationToken)
    {
        var accounts = await links.ListAsync(request.GuildId);
        ulong primary = accounts.FirstOrDefault(link => link.AlternateId == request.UserId)?.PrimaryId ?? request.UserId;
        ulong? owner = request.Scope switch
        {
            "event" => (await events.GetAsync(request.GuildId, request.Target))?.Event.OrganizerId,
            "group" => (await groups.ListAsync(request.GuildId)).FirstOrDefault(group => group.Id == request.Target)?.OrganizerId,
            _ => null
        };
        return owner is not null && (owner == primary || request.Administrator);
    }

    public async Task<Result<ulong[]>> Handle(VoiceParticipantsQuery request, CancellationToken cancellationToken)
    {
        var connected = request.ConnectedAccounts.ToHashSet();
        foreach (var account in await links.ListAsync(request.GuildId))
            if (connected.Contains(account.AlternateId)) connected.Add(account.PrimaryId);
        return connected.ToArray();
    }
}
