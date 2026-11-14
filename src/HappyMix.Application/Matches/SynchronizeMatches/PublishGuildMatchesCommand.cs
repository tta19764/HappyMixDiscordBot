using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Domain.Abstractions;
using HappyMix.Domain.Esports;

namespace HappyMix.Application.Matches.SynchronizeMatches;

public interface IGuildAnnouncementPublisher
{
    IMatchAnnouncementPublisher ForGuild(GuildSettings settings);
}

public sealed record PublishGuildMatchesCommand(GuildSettings Destination, EsportsMatchDay Matches,
    DateTimeOffset Now) : ICommand;

internal sealed class PublishGuildMatchesCommandHandler(IClock clock, IPandaScoreClient pandaScore,
    IGuildAnnouncementPublisher publisher, IMatchAnnouncementStateFactory stateFactory)
    : ICommandHandler<PublishGuildMatchesCommand>
{
    public async Task<Result> Handle(PublishGuildMatchesCommand command, CancellationToken cancellationToken)
    {
        var target = command.Destination;
        var publication = new SynchronizeMatchesCommandHandler(clock, pandaScore, publisher.ForGuild(target),
            stateFactory.ForChannel(target.ChannelId));
        await publication.PublishAsync(command.Matches, command.Now,
            target.Preferences ?? GuildEsportsPreferences.Default,
            TimeZoneInfo.FindSystemTimeZoneById(target.TimeZoneId), cancellationToken);
        return Result.Success();
    }
}
