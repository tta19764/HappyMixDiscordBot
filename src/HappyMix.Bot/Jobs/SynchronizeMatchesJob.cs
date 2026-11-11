using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Application.Matches.SynchronizeMatches;
using HappyMix.Bot.Configuration;
using HappyMix.Bot.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace HappyMix.Bot.Jobs;

/// <summary>Polls the followed main-team CS2 and Dota 2 feeds without overlapping executions.</summary>
/// <param name="sender">Dispatches the synchronization use case.</param>
/// <param name="logger">Records successful synchronization.</param>
[DisallowConcurrentExecution]
internal sealed partial class SynchronizeMatchesJob(
    ISender sender,
    ILogger<SynchronizeMatchesJob> logger,
    IClock clock,
    IPandaScoreClient pandaScore,
    IGuildMatchFeed guildFeed,
    IGuildSettingsStore settingsStore,
    IMatchAnnouncementStateFactory stateFactory,
    DiscordMatchAnnouncementPublisher publisher,
    IOptions<DiscordOptions> discord) : IJob
{
    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var saved = await settingsStore.ListAsync(cancellationToken);
        var destinations = saved.Where(item => item.ChannelId != 0).ToList();
        var errors = new List<Exception>();
        ulong legacyChannel = discord.Value.AnnouncementChannelId;
        if (legacyChannel != 0 && destinations.All(item => item.ChannelId != legacyChannel))
        {
            try
            {
                var legacyGuildId = await publisher.GetLegacyGuildIdAsync(cancellationToken);
                var settings = saved.FirstOrDefault(item => item.GuildId == legacyGuildId);
                if (settings is null)
                {
                    var result = await sender.Send(new SynchronizeMatchesCommand(), cancellationToken);
                    if (result.IsFailure)
                        throw new JobExecutionException(result.Error.Description);
                }
                else if (settings.ChannelId == 0)
                {
                    destinations.Add(settings with { ChannelId = legacyChannel });
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(exception);
            }
        }

        var now = clock.UtcNow;
        foreach (var settings in destinations)
        {
            var preferences = settings.Preferences ?? GuildEsportsPreferences.Default;
            if (preferences.Notifications == EsportsNotifications.None)
                continue;

            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
                var matches = await guildFeed.GetMatchesAsync(now, settings.TimeZoneId, preferences, cancellationToken);
                var handler = new SynchronizeMatchesCommandHandler(clock, pandaScore, publisher.ForGuild(settings),
                    stateFactory.ForChannel(settings.ChannelId));
                await handler.PublishAsync(matches, now, preferences, zone, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(new InvalidOperationException($"Match synchronization failed for guild {settings.GuildId}.", exception));
            }
        }

        if (errors.Count > 0)
            throw new JobExecutionException(new AggregateException(errors));

        LogSynchronizationCompleted(logger);
    }

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "PandaScore match synchronization completed")]
    private static partial void LogSynchronizationCompleted(ILogger logger);
}
