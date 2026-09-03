using Discord;
using Discord.Interactions;
using Discord.WebSocket;
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
            .Bind(configuration.GetSection(DiscordOptions.SectionName));

        // Guilds is the minimum gateway intent required by the current slash-command-only bot.
        // Add privileged intents here only when a feature actually needs them.
        services.AddSingleton(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            LogGatewayIntentWarnings = true
        });

        // DiscordSocketClient and InteractionService hold connection and command state,
        // so one shared instance of each must be used for the entire process lifetime.
        services.AddSingleton<DiscordSocketClient>();
        services.AddSingleton(static provider =>
            new InteractionService(provider.GetRequiredService<DiscordSocketClient>().Rest));

        // The hosted service owns only the persistent Discord connection lifecycle.
        services.AddHostedService<DiscordBotWorker>();
    }

    private static void AddScheduledJobs(this IServiceCollection services)
    {
        services.AddQuartz(static scheduler =>
        {
            // A stable identity prevents accidental duplicate registrations when
            // persistent Quartz storage is introduced in the future.
            JobKey jobKey = new("bot-health-check");

            scheduler.AddJob<BotHealthCheckJob>(job => job.WithIdentity(jobKey));
            scheduler.AddTrigger(trigger => trigger
                .ForJob(jobKey)
                .WithIdentity("bot-health-check-trigger")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(TimeSpan.FromMinutes(15))
                    .RepeatForever()));
        });

        // Allow an executing job to finish during graceful application shutdown.
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    }
}
