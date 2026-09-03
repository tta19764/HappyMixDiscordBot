using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application;
using HappyMix.Bot;
using HappyMix.Bot.Configuration;
using HappyMix.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<DiscordOptions>()
    .Bind(builder.Configuration.GetSection(DiscordOptions.SectionName));

builder.Services.AddSingleton(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds,
    LogGatewayIntentWarnings = true
});
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddSingleton(static provider =>
    new InteractionService(provider.GetRequiredService<DiscordSocketClient>().Rest));

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHostedService<DiscordBotWorker>();
builder.Services.AddQuartz(static scheduler =>
{
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
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

await builder.Build().RunAsync();
