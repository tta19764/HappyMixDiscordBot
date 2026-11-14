using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public sealed record GetUpcomingAvailabilityQuery(ulong GuildId, ulong UserId) : IQuery<AvailabilityWindow[]>;
public sealed record SaveAvailabilityCommand(ulong GuildId, ulong UserId, Guid? Id, DateTimeOffset Start,
    DateTimeOffset End, bool Tentative = false) : ICommand<AvailabilityWindow>;
public sealed record RemoveAvailabilityCommand(ulong GuildId, ulong UserId, Guid Id) : ICommand;

internal sealed class AvailabilityHandlers(IAvailabilityStore store, IClock clock, IAccountLinkStore accountLinks) :
    IQueryHandler<GetUpcomingAvailabilityQuery, AvailabilityWindow[]>,
    ICommandHandler<SaveAvailabilityCommand, AvailabilityWindow>,
    ICommandHandler<RemoveAvailabilityCommand>
{
    public async Task<Result<AvailabilityWindow[]>> Handle(GetUpcomingAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var windows = await store.ListAsync(query.GuildId, await MemberAsync(query.GuildId, query.UserId));
        return Result.Success(windows.Where(window => window.End > clock.UtcNow).ToArray());
    }

    public async Task<Result<AvailabilityWindow>> Handle(SaveAvailabilityCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return Result.Success(await store.SaveAsync(command.GuildId, await MemberAsync(command.GuildId, command.UserId), command.Id, command.Start, command.End, command.Tentative));
        }
        catch (AvailabilityException exception)
        {
            return Result.Failure<AvailabilityWindow>(new("Availability.Invalid", exception.Message));
        }
    }

    public async Task<Result> Handle(RemoveAvailabilityCommand command, CancellationToken cancellationToken) =>
        await store.RemoveAsync(command.GuildId, await MemberAsync(command.GuildId, command.UserId), command.Id)
            ? Result.Success()
            : Result.Failure(new("Availability.NotFound", "Availability window not found."));

    /// <summary>Linked accounts share one schedule, stored on the family's primary account.</summary>
    private async Task<ulong> MemberAsync(ulong guildId, ulong userId) => (await accountLinks.LoadAsync(guildId)).Member(userId);
}
