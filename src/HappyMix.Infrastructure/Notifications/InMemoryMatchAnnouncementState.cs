using System.Collections.Concurrent;
using HappyMix.Application.Abstractions.Notifications;

namespace HappyMix.Infrastructure.Notifications;

internal sealed class InMemoryMatchAnnouncementState : IMatchAnnouncementState
{
    private readonly ConcurrentDictionary<DateOnly, byte> _publishedSchedules = new();
    private readonly ConcurrentDictionary<long, byte> _startingSoonMatches = new();
    private readonly ConcurrentDictionary<long, byte> _publishedMapResults = new();
    private readonly ConcurrentDictionary<long, byte> _publishedMatchResults = new();

    public bool WasSchedulePublished(DateOnly matchDate) => _publishedSchedules.ContainsKey(matchDate);

    public bool WasStartingSoonPublished(long matchId) => _startingSoonMatches.ContainsKey(matchId);

    public bool WasMapResultPublished(long gameId) => _publishedMapResults.ContainsKey(gameId);

    public bool WasMatchResultPublished(long matchId) => _publishedMatchResults.ContainsKey(matchId);

    public void MarkSchedulePublished(DateOnly matchDate) => _publishedSchedules.TryAdd(matchDate, 0);

    public void MarkStartingSoonPublished(long matchId) => _startingSoonMatches.TryAdd(matchId, 0);

    public void MarkMapResultPublished(long gameId) => _publishedMapResults.TryAdd(gameId, 0);

    public void MarkMatchResultPublished(long matchId) => _publishedMatchResults.TryAdd(matchId, 0);
}
