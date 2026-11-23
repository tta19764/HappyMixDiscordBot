using HappyMix.Application.Abstractions.Guilds;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Guilds;
using HappyMix.Application.Matches.SynchronizeMatches;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Bot.Jobs;
using HappyMix.Bot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace HappyMix.Bot.Configuration;

/// <summary>
/// Provides dependency-injection registrations for the Discord adapter and scheduled jobs.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds all services required to host the Discord bot and its Quartz jobs.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <param name="configuration">The application configuration containing Discord settings.</param>
    /// <returns>The same service collection so registrations can be chained.</returns>
    public static IServiceCollection AddBot(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDiscord(configuration);
        services.AddScheduledJobs();

        return services;
    }

    private static void AddDiscord(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options bind from appsettings and environment variables. For example,
        // Discord__Token overrides Discord:Token without placing a secret in source control.
        services
            .AddOptions<DiscordOptions>()
            .Bind(configuration.GetSection(DiscordOptions.SectionName))
            .Validate(options => options.MaxFollowedTeamsPerGame > 0 && options.MaxTwitchChannelsPerGame > 0,
                "Discord subscription limits must be positive.")
            .ValidateOnStart();

        // Stored logs for the admin Logs tab: the provider queues entries, the writer stores and prunes them.
        services.AddSingleton<HappyMix.Bot.Logging.DatabaseLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<HappyMix.Bot.Logging.DatabaseLoggerProvider>());
        services.AddHostedService<HappyMix.Bot.Logging.DatabaseLogWriter>();

        // Starting values for servers that haven't chosen their own language or broadcast preferences.
        services.AddOptions<CommunityDefaults>()
            .Bind(configuration.GetSection("CommunityDefaults"))
            .Validate(options => CommunityDefaults.Normalize(options.Language) is not null && options.Discovery().IsValid,
                "CommunityDefaults needs Language en, uk or ru and valid stream preference lists.")
            .ValidateOnStart();

        // Guilds is the minimum gateway intent required by the current slash-command-only bot.
        // Add privileged intents here only when a feature actually needs them.
        services.AddSingleton(new DiscordSocketConfig
        {
            // GuildMessagePolls delivers poll votes so Discord polls stay in step with the dashboard; it isn't privileged.
            // GuildPresences (privileged) only when Live should read Discord "Playing" statuses: it must also be switched on
            // in the Discord Developer Portal, or Discord refuses the connection.
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMessagePolls
                | (LivePresenceWatcher.Enabled(configuration) ? GatewayIntents.GuildPresences : GatewayIntents.None),
            LogGatewayIntentWarnings = true
        });

        // DiscordSocketClient and InteractionService hold connection and command state,
        // so one shared instance of each must be used for the entire process lifetime.
        services.AddSingleton<DiscordSocketClient>();
        services.AddSingleton<IGroupRoleGateway, DiscordGroupRoleGateway>();
        services.AddSingleton<IMemberDirectory, DiscordMemberDirectory>();
        services.AddSingleton<IGuildMemberGateway, DiscordGuildMemberGateway>();
        services.AddSingleton<HappyMix.Application.Abstractions.Events.IEventNotifier, DiscordEventNotifier>();
        services.AddSingleton(static provider =>
            new InteractionService(provider.GetRequiredService<DiscordSocketClient>().Rest));
        services.AddHttpClient("Twitch", client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<TwitchLiveStreams>();
        services.AddHttpClient("SteamNews", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(SteamArt.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HappyMixBot/1.0");
        });
        services.AddSingleton<SteamArt>();
        services.AddSingleton<GameNewsService>();
        services.AddSingleton<HappyMix.Application.Abstractions.News.IGameNewsGateway, DiscordGameNewsGateway>();
        services.AddSingleton<DiscordLiveNotifier>();
        services.AddSingleton<HappyMix.Application.Abstractions.Live.ILiveNotifier>(provider => provider.GetRequiredService<DiscordLiveNotifier>());
        services.AddSingleton<HappyMix.Application.Abstractions.Quiet.IHeldMessageSender>(provider => provider.GetRequiredService<DiscordLiveNotifier>());
        services.AddSingleton<HappyMix.Application.Abstractions.Quiet.IHeldMessageSender, HeldGroupInviteSender>();
        services.AddHostedService<LivePresenceWatcher>();
        services.AddSingleton<NotificationChannels>();
        services.AddSingleton<EventNotificationSettings>();
        services.AddSingleton<PollMessages>();
        services.AddSingleton<DiscordGroupNotifier>();
        services.AddSingleton<IGroupNotifier>(provider => provider.GetRequiredService<DiscordGroupNotifier>());
        services.AddSingleton<HappyMix.Application.Abstractions.Stats.IRecapPublisher, DiscordRecapPublisher>();
        services.AddHttpClient(HeroEmojis.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8));
        services.AddSingleton<HeroEmojis>();
        services.AddSingleton<HappyMix.Application.Abstractions.Stats.IDotaMetaPublisher, DiscordDotaMetaPublisher>();
        services.AddSingleton<HappyMix.Application.Abstractions.Quiet.IHeldMessageSender>(provider => provider.GetRequiredService<DiscordGroupNotifier>());
        services.AddSingleton<DiscordMatchAnnouncementPublisher>();
        services.AddSingleton<IGuildAnnouncementPublisher>(provider =>
            provider.GetRequiredService<DiscordMatchAnnouncementPublisher>());
        services.AddSingleton<IMatchAnnouncementPublisher>(provider =>
            provider.GetRequiredService<DiscordMatchAnnouncementPublisher>());

        // The hosted service owns only the persistent Discord connection lifecycle.
        services.AddHostedService<DiscordBotWorker>();
    }

    private static void AddScheduledJobs(this IServiceCollection services)
    {
        services.AddQuartz(static scheduler =>
        {
            // A stable identity prevents accidental duplicate registrations when
            // persistent Quartz storage is introduced in the future.
            JobKey jobKey = new("synchronize-esports-matches");

            scheduler.AddJob<SynchronizeMatchesJob>(job => job.WithIdentity(jobKey));
            scheduler.AddTrigger(trigger => trigger
                .ForJob(jobKey)
                .WithIdentity("synchronize-esports-matches-trigger")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(TimeSpan.FromMinutes(1))
                    .RepeatForever()));

            JobKey newsKey = new("publish-game-news");
            scheduler.AddJob<PublishGameNewsJob>(job => job.WithIdentity(newsKey));
            JobKey statsKey = new("refresh-game-stats");
            scheduler.AddJob<RefreshGameStatsJob>(job => job.WithIdentity(statsKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(statsKey).StartAt(DateBuilder.FutureDate(2, IntervalUnit.Minute))
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(10).RepeatForever()));

            JobKey metaKey = new("post-dota-meta");
            scheduler.AddJob<RunDotaMetaScheduleJob>(job => job.WithIdentity(metaKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(metaKey).StartAt(DateBuilder.FutureDate(8, IntervalUnit.Minute))
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(60).RepeatForever()));

            JobKey digestKey = new("post-weekly-digests");
            scheduler.AddJob<PostWeeklyDigestsJob>(job => job.WithIdentity(digestKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(digestKey).StartAt(DateBuilder.FutureDate(6, IntervalUnit.Minute))
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(60).RepeatForever()));

            JobKey matchesKey = new("refresh-matches");
            scheduler.AddJob<RefreshMatchesJob>(job => job.WithIdentity(matchesKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(matchesKey).StartAt(DateBuilder.FutureDate(4, IntervalUnit.Minute))
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(10).RepeatForever()));

            JobKey heldKey = new("release-held-messages");
            scheduler.AddJob<ReleaseHeldMessagesJob>(job => job.WithIdentity(heldKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(heldKey).StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));

            JobKey liveKey = new("sync-steam-playing");
            scheduler.AddJob<SyncSteamPlayingJob>(job => job.WithIdentity(liveKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(liveKey).StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(2).RepeatForever()));
            scheduler.AddTrigger(trigger => trigger.ForJob(newsKey).StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(10).RepeatForever()));

            JobKey releasesKey = new("publish-release-notes");
            scheduler.AddJob<PublishReleaseNotesJob>(job => job.WithIdentity(releasesKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(releasesKey).StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));

            JobKey channelEventsKey = new("publish-event-channels");
            scheduler.AddJob<PublishEventChannelsJob>(job => job.WithIdentity(channelEventsKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(channelEventsKey).StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));

            JobKey pollsKey = new("publish-polls");
            scheduler.AddJob<PublishPollsJob>(job => job.WithIdentity(pollsKey));
            scheduler.AddTrigger(trigger => trigger.ForJob(pollsKey).StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));

            JobKey eventsKey = new("dispatch-event-messages");
            scheduler.AddJob<DispatchEventMessagesJob>(job => job.WithIdentity(eventsKey));
            scheduler.AddTrigger(trigger => trigger
                .ForJob(eventsKey)
                .WithIdentity("dispatch-event-messages-trigger")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(TimeSpan.FromMinutes(1))
                    .RepeatForever()));
        });

        // Allow an executing job to finish during graceful application shutdown.
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    }
}
