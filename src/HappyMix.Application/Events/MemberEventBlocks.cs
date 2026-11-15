using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Events;

/// <summary>
/// A confirmed event date one member is going to. <see cref="UserId"/> is the account from the query's member list.
/// <see cref="Visible"/> is false when the viewer isn't part of the event: then only "busy with an event" may be shown,
/// not the title.
/// </summary>
public sealed record MemberEventBlock(ulong UserId, Guid EventId, DateOnly Date, string Title, DateTimeOffset Start, DateTimeOffset End, bool Visible);

/// <summary>
/// Confirmed event dates overlapping <paramref name="From"/>–<paramref name="Until"/> for each of <paramref name="Members"/>:
/// the organizer and every invitee who accepted that date.
/// </summary>
public sealed record GetMembersEventsQuery(ulong GuildId, ulong ViewerId, IReadOnlyCollection<ulong> Members, DateTimeOffset From,
    DateTimeOffset Until) : IQuery<IReadOnlyList<MemberEventBlock>>;

internal sealed class MemberEventBlockQueries(IEventStore store, IAccountLinkStore accountLinks)
    : IQueryHandler<GetMembersEventsQuery, IReadOnlyList<MemberEventBlock>>
{
    public async Task<Result<IReadOnlyList<MemberEventBlock>>> Handle(GetMembersEventsQuery query, CancellationToken cancellationToken)
    {
        var links = await accountLinks.LoadAsync(query.GuildId);
        ulong viewer = links.Member(query.ViewerId);
        // Board rows use the account from the member list; events store the primary account of linked families.
        var accounts = query.Members.GroupBy(links.Member).ToDictionary(group => group.Key, group => group.First());
        var blocks = new List<MemberEventBlock>();
        foreach (var details in await store.ListConfirmedAsync(query.GuildId, query.From, query.Until))
        {
            bool visible = details.Event.OrganizerId == viewer || details.Invitees.Contains(viewer);
            foreach (var occurrence in details.Occurrences.Where(item => item.Status == OccurrenceStatus.Confirmed &&
                         item.End > query.From && item.Start < query.Until))
            {
                var going = details.Responses.Where(item => item.Date == occurrence.Date && item.Accepted && details.Invitees.Contains(item.UserId))
                    .Select(item => item.UserId).Append(details.Event.OrganizerId).Distinct();
                foreach (ulong member in going)
                    if (accounts.TryGetValue(member, out ulong account))
                        blocks.Add(new MemberEventBlock(account, details.Event.Id, occurrence.Date, details.Event.Title, occurrence.Start, occurrence.End, visible));
            }
        }
        return Result.Success<IReadOnlyList<MemberEventBlock>>(blocks.OrderBy(item => item.Start).ToArray());
    }
}
