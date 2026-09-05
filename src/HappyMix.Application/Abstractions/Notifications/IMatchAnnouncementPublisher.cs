using HappyMix.Domain.CounterStrike;

namespace HappyMix.Application.Abstractions.Notifications;

public interface IMatchAnnouncementPublisher
{
    Task PublishDailyScheduleAsync(
        DateOnly matchDate,
        IReadOnlyCollection<CounterStrikeMatch> matches,
        CancellationToken cancellationToken);

    Task PublishStartingSoonAsync(CounterStrikeMatch match, CancellationToken cancellationToken);

    Task PublishMapResultAsync(
        CounterStrikeMatch match,
        CounterStrikeGame game,
        CancellationToken cancellationToken);

    Task PublishMatchResultAsync(CounterStrikeMatch match, CancellationToken cancellationToken);
}
