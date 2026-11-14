using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public sealed record LinkedAccountFamily(ulong PrimaryId, AccountLink[] Alternates);
public sealed record GetServerAccountLinksQuery(ulong GuildId, ulong AdministratorId) : IQuery<LinkedAccountFamily[]>;

internal sealed class ServerAccountLinksQueries(IAccountLinkStore links, IGuildMemberGateway members)
    : IQueryHandler<GetServerAccountLinksQuery, LinkedAccountFamily[]>
{
    public async Task<Result<LinkedAccountFamily[]>> Handle(GetServerAccountLinksQuery query, CancellationToken cancellationToken)
    {
        if (!await members.IsAdministratorAsync(query.GuildId, query.AdministratorId))
            return Result.Failure<LinkedAccountFamily[]>(new("Link.Forbidden", "Only server administrators can view all linked accounts."));

        var saved = await links.ListAsync(query.GuildId);
        return Result.Success(saved.GroupBy(link => link.PrimaryId).OrderBy(group => group.Key)
            .Select(group => new LinkedAccountFamily(group.Key,
                group.OrderBy(link => link.LinkedAt).ThenBy(link => link.AlternateId).ToArray())).ToArray());
    }
}
