using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Application.Matches.SynchronizeMatches;
using HappyMix.Bot.Configuration;
using HappyMix.Bot.Services;
using HappyMix.Domain.Esports;
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
    IGuildMatchFeed guildFeed,
    DiscordMatchAnnouncementPublisher publisher,
    IOptions<DiscordOptions> discord) : IJob
{
    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var saved = (await sender.Send(new ListGuildSettingsQuery(), cancellationToken)).Value;
        var destinations = saved.Where(item => GetRoutes(item).Count > 0).ToList();
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
                    destinations.RemoveAll(destination => destination.GuildId == settings.GuildId);
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
            if (preferences.Notifications == EsportsNotifications.None && !preferences.MatchStarts)
                continue;

            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
                var matches = await guildFeed.GetMatchesAsync(now, settings.TimeZoneId, preferences, cancellationToken);
                foreach (var route in GetRoutes(settings))
                {
                    var target = settings with { ChannelId = route.Key };
                    var channelMatches = matches with
                    {
                        Matches = matches.Matches.Where(match => route.Contains(match.Game)).ToArray()
                    };
                    try
                    {
                        var result = await sender.Send(new PublishGuildMatchesCommand(target, channelMatches, now), cancellationToken);
                        if (result.IsFailure)
                            throw new InvalidOperationException(result.Error.Description);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors.Add(new InvalidOperationException($"Match publication failed for channel {route.Key}.", exception));
                    }
                }
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

    internal static IReadOnlyList<IGrouping<ulong, Esport>> GetRoutes(GuildSettings settings)
    {
        var routes = new[]
        {
            (Game: Esport.CounterStrike2, Channel: settings.Preferences?.CounterStrikeChannelId ?? settings.ChannelId),
            (Game: Esport.Dota2, Channel: settings.Preferences?.Dota2ChannelId ?? settings.ChannelId)
        };
        return routes.Where(route => route.Channel != 0).GroupBy(route => route.Channel, route => route.Game).ToArray();
    }

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "PandaScore match synchronization completed")]
    private static partial void LogSynchronizationCompleted(ILogger logger);
}
