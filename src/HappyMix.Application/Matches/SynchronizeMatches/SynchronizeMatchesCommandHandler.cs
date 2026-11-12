using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Domain.Abstractions;
using HappyMix.Domain.Esports;

namespace HappyMix.Application.Matches.SynchronizeMatches;

/// <summary>
/// Compares PandaScore's current match state with published announcements and emits new events.
/// </summary>
/// <param name="clock">Supplies a testable current time.</param>
/// <param name="pandaScore">Provides followed main-team fixtures for CS2 and Dota 2.</param>
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
        await PublishAsync(matchDay, now, GuildEsportsPreferences.Default, null, cancellationToken);
        return Result.Success();
    }

    public async Task PublishAsync(EsportsMatchDay matchDay, DateTimeOffset now,
        GuildEsportsPreferences preferences, TimeZoneInfo? timeZone, CancellationToken cancellationToken)
    {
        state.Initialize(matchDay.Matches);

        if (preferences.Notifications.HasFlag(EsportsNotifications.Schedule) && !state.WasSchedulePublished(matchDay.Date))
        {
            var schedule = timeZone is null ? matchDay.Matches : matchDay.Matches.Where(match =>
                !match.IsScheduled || match.BeginAt is not { } start ||
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, timeZone).DateTime) == matchDay.Date).ToArray();
            await publisher.PublishDailyScheduleAsync(matchDay.Date, schedule, cancellationToken);
            state.MarkSchedulePublished(matchDay.Date);
        }

        foreach (var match in matchDay.Matches)
        {
            if (preferences.Notifications.HasFlag(EsportsNotifications.Reminders))
                await PublishStartingSoonIfRequiredAsync(match, now, preferences.ReminderMinutes, cancellationToken);
            if (preferences.Notifications.HasFlag(EsportsNotifications.Maps))
                await PublishMapResultsIfRequiredAsync(match, cancellationToken);
            if (preferences.Notifications.HasFlag(EsportsNotifications.Results))
                await PublishMatchResultIfRequiredAsync(match, cancellationToken);
        }
    }

    private async Task PublishStartingSoonIfRequiredAsync(
        EsportsMatch match,
        DateTimeOffset now,
        int reminderMinutes,
        CancellationToken cancellationToken)
    {
        if (match.BeginAt is not { } beginAt ||
            !match.IsScheduled ||
            state.WasStartingSoonPublished(match.Game, match.Id))
        {
            return;
        }

        TimeSpan untilStart = beginAt - now;
        if (untilStart <= TimeSpan.Zero || untilStart > TimeSpan.FromMinutes(reminderMinutes))
        {
            return;
        }

        await publisher.PublishStartingSoonAsync(match, cancellationToken);
        state.MarkStartingSoonPublished(match.Game, match.Id);
    }

    private async Task PublishMapResultsIfRequiredAsync(
        EsportsMatch match,
        CancellationToken cancellationToken)
    {
        if (!match.IsRunning && !match.IsFinished)
        {
            return;
        }

        foreach (var game in match.Games.Where(game => game.HasResult))
        {
            if (state.WasMapResultPublished(match.Game, game.Id))
            {
                continue;
            }

            await publisher.PublishMapResultAsync(match, game, cancellationToken);
            state.MarkMapResultPublished(match.Game, game.Id);
        }
    }

    private async Task PublishMatchResultIfRequiredAsync(
        EsportsMatch match,
        CancellationToken cancellationToken)
    {
        if (!match.IsFinished || state.WasMatchResultPublished(match.Game, match.Id))
        {
            return;
        }

        await publisher.PublishMatchResultAsync(match, cancellationToken);
        state.MarkMatchResultPublished(match.Game, match.Id);
    }
}
