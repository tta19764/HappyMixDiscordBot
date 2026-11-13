using System.Globalization;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Domain.Esports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Stores game-qualified delivery history in SQLite using short-lived contexts.</summary>
/// <param name="factory">Creates independent units of work for concurrent callers.</param>
/// <param name="options">Configures first-run replay policy.</param>
/// <param name="scope">Destination channel ID; switching destinations does not reuse another channel's history.</param>
/// <remarks>
/// A single bot instance publishes serially. Unique keys make writes idempotent, but do not make
/// sending to Discord and recording delivery atomic. A crash between send and save may cause a replay.
/// </remarks>
internal sealed class SqliteMatchAnnouncementState(
    IDbContextFactory<HappyMixDbContext> factory, IOptions<StorageOptions> options,
    string scope) : IMatchAnnouncementState
{
    /// <inheritdoc />
    public void Initialize(IReadOnlyCollection<EsportsMatch> matches)
    {
        using var context = factory.CreateDbContext();
        using var transaction = context.Database.BeginTransaction();
        if (context.Announcements.Any(item => item.Scope == scope && item.Key == "initialized"))
        {
            return;
        }

        if (!options.Value.ReplayExistingResultsOnFirstRun)
        {
            foreach (var match in matches.Where(match => match.IsRunning || match.IsFinished))
            {
                foreach (var game in match.Games.Where(game => game.HasResult))
                {
                    Insert(context, EventKey("map", match.Game, game.Id));
                }

                if (match.IsFinished)
                {
                    Insert(context, EventKey("result", match.Game, match.Id));
                }
            }
        }

        Insert(context, "initialized");
        transaction.Commit();
    }

    /// <inheritdoc />
    public bool WasSchedulePublished(DateOnly matchDate) => Contains(ScheduleKey(matchDate));
    /// <inheritdoc />
    public bool WasStartingSoonPublished(Esport game, long matchId) => Contains(EventKey("start", game, matchId));
    /// <inheritdoc />
    public bool WasMatchStartedPublished(Esport game, long matchId) => Contains(EventKey("live", game, matchId));
    /// <inheritdoc />
    public void MarkMatchStartedPublished(Esport game, long matchId) => Mark(EventKey("live", game, matchId));
    /// <inheritdoc />
    public bool WasMapResultPublished(Esport game, long gameId) => Contains(EventKey("map", game, gameId));
    /// <inheritdoc />
    public bool WasMatchResultPublished(Esport game, long matchId) => Contains(EventKey("result", game, matchId));
    /// <inheritdoc />
    public void MarkSchedulePublished(DateOnly matchDate) => Mark(ScheduleKey(matchDate));
    /// <inheritdoc />
    public void MarkStartingSoonPublished(Esport game, long matchId) => Mark(EventKey("start", game, matchId));
    /// <inheritdoc />
    public void MarkMapResultPublished(Esport game, long gameId) => Mark(EventKey("map", game, gameId));
    /// <inheritdoc />
    public void MarkMatchResultPublished(Esport game, long matchId) => Mark(EventKey("result", game, matchId));

    private bool Contains(string key)
    {
        using var context = factory.CreateDbContext();
        return context.Announcements.Any(item => item.Scope == scope && item.Key == key);
    }

    private void Mark(string key)
    {
        using var context = factory.CreateDbContext();
        Insert(context, key);
    }

    private void Insert(HappyMixDbContext context, string key)
    {
        // The conflict target suppresses duplicate markers, never unrelated database errors.
        context.Database.ExecuteSqlInterpolated($"""
            INSERT INTO "AnnouncementRecords" ("Scope", "Key", "RecordedAtUtc")
            VALUES ({scope}, {key}, {DateTime.UtcNow})
            ON CONFLICT ("Scope", "Key") DO NOTHING
            """);
    }

    private static string ScheduleKey(DateOnly date) =>
        "schedule:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string EventKey(string kind, Esport game, long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}:{(int)game}:{id}");
}
