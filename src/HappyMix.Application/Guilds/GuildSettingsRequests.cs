using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Domain.Abstractions;

namespace HappyMix.Application.Guilds;

public sealed record GetGuildSettingsQuery(ulong GuildId) : IQuery<GuildSettings?>;
public sealed record ListGuildSettingsQuery : IQuery<IReadOnlyList<GuildSettings>>;
public sealed record UpdateGuildSettingsCommand(ulong GuildId, string DefaultTimeZone,
    Func<GuildSettings, GuildSettings> Update) : ICommand;

internal sealed class GuildSettingsHandlers(IGuildSettingsStore store) :
    IQueryHandler<GetGuildSettingsQuery, GuildSettings?>,
    IQueryHandler<ListGuildSettingsQuery, IReadOnlyList<GuildSettings>>,
    ICommandHandler<UpdateGuildSettingsCommand>
{
    public async Task<Result<GuildSettings?>> Handle(GetGuildSettingsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.GetAsync(query.GuildId, cancellationToken));

    public async Task<Result<IReadOnlyList<GuildSettings>>> Handle(ListGuildSettingsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.ListAsync(cancellationToken));

    public async Task<Result> Handle(UpdateGuildSettingsCommand command, CancellationToken cancellationToken)
    {
        await store.UpdateAsync(command.GuildId, command.DefaultTimeZone, command.Update, cancellationToken);
        return Result.Success();
    }
}
