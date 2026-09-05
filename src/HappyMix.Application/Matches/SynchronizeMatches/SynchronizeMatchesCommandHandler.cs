using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Domain.Abstractions;
using HappyMix.Domain.CounterStrike;

namespace HappyMix.Application.Matches.SynchronizeMatches;

/// <summary>
/// Compares PandaScore's current match state with published announcements and emits new events.
/// </summary>
/// <param name="clock">Supplies a testable current time.</param>
/// <param name="pandaScore">Provides Counter-Strike fixture and result data.</param>
/// <param name="publisher">Publishes provider-neutral match announcements.</param>
/// <param name="state">Tracks which announcements have already been published.</param>
public sealed class SynchronizeMatchesCommandHandler(
    IClock clock,
    IPandaScoreClient pandaScore,
    IMatchAnnouncementPublisher publisher,
    IMatchAnnouncementState state) : ICommandHandler<SynchronizeMatchesCommand>
{
    /// <summary>
    /// Publishes the daily schedule once, imminent-match warnings, newly completed maps, and final results.
    /// </summary>
    /// <param name="request">The synchronization command.</param>
    /// <param name="cancellationToken">The token used to cancel API calls and message publication.</param>
    /// <returns>A successful result after all currently required announcements are published.</returns>
    public async Task<Result> Handle(
        SynchronizeMatchesCommand request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        var matchDay = await pandaScore.GetMatchesForDayAsync(now, cancellationToken);

        if (!state.WasSchedulePublished(matchDay.Date))
        {
            await publisher.PublishDailyScheduleAsync(matchDay.Date, matchDay.Matches, cancellationToken);
            state.MarkSchedulePublished(matchDay.Date);
        }

        foreach (var match in matchDay.Matches)
        {
            await PublishStartingSoonIfRequiredAsync(match, now, cancellationToken);
            await PublishMapResultsIfRequiredAsync(match, cancellationToken);
            await PublishMatchResultIfRequiredAsync(match, cancellationToken);
        }

        return Result.Success();
    }

    private async Task PublishStartingSoonIfRequiredAsync(
        CounterStrikeMatch match,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (match.BeginAt is not { } beginAt ||
            match.IsFinished ||
            state.WasStartingSoonPublished(match.Id))
        {
            return;
        }

        TimeSpan untilStart = beginAt - now;
        if (untilStart <= TimeSpan.Zero || untilStart > TimeSpan.FromMinutes(30))
        {
            return;
        }

        await publisher.PublishStartingSoonAsync(match, cancellationToken);
        state.MarkStartingSoonPublished(match.Id);
    }

    private async Task PublishMapResultsIfRequiredAsync(
        CounterStrikeMatch match,
        CancellationToken cancellationToken)
    {
        if (!match.IsRunning)
        {
            return;
        }

        foreach (var game in match.Games.Where(game => game.Complete))
        {
            if (state.WasMapResultPublished(game.Id))
            {
                continue;
            }

            await publisher.PublishMapResultAsync(match, game, cancellationToken);
            state.MarkMapResultPublished(game.Id);
        }
    }

    private async Task PublishMatchResultIfRequiredAsync(
        CounterStrikeMatch match,
        CancellationToken cancellationToken)
    {
        if (!match.IsFinished || state.WasMatchResultPublished(match.Id))
        {
            return;
        }

        await publisher.PublishMatchResultAsync(match, cancellationToken);
        state.MarkMatchResultPublished(match.Id);
    }
}
