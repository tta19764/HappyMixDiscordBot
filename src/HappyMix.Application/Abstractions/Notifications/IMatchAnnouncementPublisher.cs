using HappyMix.Domain.Esports;

namespace HappyMix.Application.Abstractions.Notifications;

/// <summary>Publishes discipline-labelled schedules and series/game notifications.</summary>
public interface IMatchAnnouncementPublisher
{
    /// <summary>Publishes a combined schedule grouped by discipline and tournament.</summary>
    Task PublishDailyScheduleAsync(
        DateOnly matchDate,
        IReadOnlyCollection<EsportsMatch> matches,
        CancellationToken cancellationToken);

    /// <summary>Publishes an imminent-series reminder.</summary>
    Task PublishStartingSoonAsync(EsportsMatch match, CancellationToken cancellationToken);

    /// <summary>Publishes a completed CS2 map or Dota 2 game result.</summary>
    Task PublishMapResultAsync(
        EsportsMatch match,
        EsportsGame game,
        CancellationToken cancellationToken);

    /// <summary>Publishes a final series result.</summary>
    Task PublishMatchResultAsync(EsportsMatch match, CancellationToken cancellationToken);
}
