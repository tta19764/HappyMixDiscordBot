namespace HappyMix.Application.Abstractions.Notifications;

public interface IMatchAnnouncementStateFactory
{
    IMatchAnnouncementState ForChannel(ulong channelId);
}
