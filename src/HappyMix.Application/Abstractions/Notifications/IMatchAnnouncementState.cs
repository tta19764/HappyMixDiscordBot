using HappyMix.Domain.Esports;

namespace HappyMix.Application.Abstractions.Notifications;

/// <summary>Tracks published announcements; provider IDs are always qualified by discipline.</summary>
public interface IMatchAnnouncementState
{
    /// <summary>Applies the configured first-run history policy once, after a successful provider fetch.</summary>
    /// <param name="matches">Current snapshots used to seed existing results without replaying them.</param>
    void Initialize(IReadOnlyCollection<EsportsMatch> matches);
    /// <summary>Checks whether the combined local-date schedule was published.</summary>
    bool WasSchedulePublished(DateOnly matchDate);
    /// <summary>Checks whether an upcoming series reminder was published.</summary>
    bool WasStartingSoonPublished(Esport game, long matchId);
    /// <summary>Checks whether an individual map/game result was published.</summary>
    bool WasMatchStartedPublished(Esport game, long matchId);

    void MarkMatchStartedPublished(Esport game, long matchId);

    bool WasMapResultPublished(Esport game, long gameId);
    /// <summary>Checks whether a series result was published.</summary>
    bool WasMatchResultPublished(Esport game, long matchId);
    /// <summary>Records publication of the combined daily schedule.</summary>
    void MarkSchedulePublished(DateOnly matchDate);
    /// <summary>Records a series reminder after successful publication.</summary>
    void MarkStartingSoonPublished(Esport game, long matchId);
    /// <summary>Records an individual map/game result after successful publication.</summary>
    void MarkMapResultPublished(Esport game, long gameId);
    /// <summary>Records a series result after successful publication.</summary>
    void MarkMatchResultPublished(Esport game, long matchId);
}
