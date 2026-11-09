using HappyMix.Domain.Esports;
using System.Collections.Concurrent;
using HappyMix.Application.Abstractions.Notifications;

namespace HappyMix.Infrastructure.Notifications;

/// <summary>Process-local delivery history with discipline-qualified IDs.</summary>
/// <remarks>Restart recovery requires the separately planned SQLite implementation.</remarks>
internal sealed class InMemoryMatchAnnouncementState : IMatchAnnouncementState
{
    private readonly ConcurrentDictionary<DateOnly, byte> _publishedSchedules = new();
    private readonly ConcurrentDictionary<(Esport Game, long Id), byte> _startingSoonMatches = new();
    private readonly ConcurrentDictionary<(Esport Game, long Id), byte> _publishedMapResults = new();
    private readonly ConcurrentDictionary<(Esport Game, long Id), byte> _publishedMatchResults = new();

    /// <inheritdoc />
    public bool WasSchedulePublished(DateOnly matchDate) => _publishedSchedules.ContainsKey(matchDate);

    /// <inheritdoc />
    public bool WasStartingSoonPublished(Esport game, long matchId) => _startingSoonMatches.ContainsKey((game, matchId));

    /// <inheritdoc />
    public bool WasMapResultPublished(Esport game, long gameId) => _publishedMapResults.ContainsKey((game, gameId));

    /// <inheritdoc />
    public bool WasMatchResultPublished(Esport game, long matchId) => _publishedMatchResults.ContainsKey((game, matchId));

    /// <inheritdoc />
    public void MarkSchedulePublished(DateOnly matchDate) => _publishedSchedules.TryAdd(matchDate, 0);

    /// <inheritdoc />
    public void MarkStartingSoonPublished(Esport game, long matchId) => _startingSoonMatches.TryAdd((game, matchId), 0);

    /// <inheritdoc />
    public void MarkMapResultPublished(Esport game, long gameId) => _publishedMapResults.TryAdd((game, gameId), 0);

    /// <inheritdoc />
    public void MarkMatchResultPublished(Esport game, long matchId) => _publishedMatchResults.TryAdd((game, matchId), 0);
}
