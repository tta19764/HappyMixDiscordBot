namespace HappyMix.Application.Abstractions.Notifications;

public interface IMatchAnnouncementState
{
    bool WasSchedulePublished(DateOnly matchDate);

    bool WasStartingSoonPublished(long matchId);

    bool WasMapResultPublished(long gameId);

    bool WasMatchResultPublished(long matchId);

    void MarkSchedulePublished(DateOnly matchDate);

    void MarkStartingSoonPublished(long matchId);

    void MarkMapResultPublished(long gameId);

    void MarkMatchResultPublished(long matchId);
}
