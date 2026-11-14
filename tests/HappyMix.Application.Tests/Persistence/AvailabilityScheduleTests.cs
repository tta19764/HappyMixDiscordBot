using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Bot.Interactions;
using HappyMix.Infrastructure.PandaScore;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HappyMix.Application.Tests.Persistence;

public sealed class AvailabilityScheduleTests
{
    [Fact]
    public async Task TodayUsesTheConfiguredLocalDateAndRollsOverMidnight()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var settings = database.Services.GetRequiredService<IGuildSettingsStore>();
        await settings.SaveAsync(new GuildSettings(1, 0, "Europe/Kyiv"));
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();

        var result = await sender.Send(new SetTodayAvailabilityCommand(1, 2, "17", "00:30", null, "UTC"));

        Assert.True(result.IsSuccess, result.Error.Description);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero), result.Value.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 22, 30, 0, TimeSpan.Zero), result.Value.End);
    }

    [Fact]
    public async Task WeeklyAvailabilityReplacesOneDayAndSurvivesRestart()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        var command = new SetWeeklyAvailabilityCommand(1, 2, DayOfWeek.Friday, "17", "00:30", "-05:00", "UTC");
        Assert.True((await sender.Send(command)).IsSuccess);
        Assert.True((await sender.Send(command with { Start = "18" })).IsSuccess);

        using var reopened = CreateServices(await database.OpenAsync());
        sender = reopened.GetRequiredService<ISender>();
        var saved = Assert.Single((await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value);
        Assert.Equal(new WeeklyAvailability(DayOfWeek.Friday, new TimeOnly(18, 0), new TimeOnly(0, 30), "UTC-05:00"), saved);
        Assert.Empty((await sender.Send(new GetWeeklyAvailabilityQuery(1, 3))).Value);
        Assert.True((await sender.Send(new ClearWeeklyAvailabilityCommand(1, 2, DayOfWeek.Friday))).IsSuccess);
        Assert.Empty((await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value);
    }

    [Fact]
    public async Task DiscordDiscoversBothSetSubcommands()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client.Rest);
        var module = await interactions.AddModuleAsync<AvailabilityModule>(services);

        var set = Assert.Single(module.SubModules);
        Assert.Equal(["timetable", "today"], set.SlashCommands.Select(command => command.Name).Order());
    }

    private static ServiceProvider CreateServices(IServiceProvider storage) => new ServiceCollection()
        .AddLogging().AddApplication()
        .AddSingleton(storage.GetRequiredService<IAvailabilityStore>())
        .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
        .AddSingleton<IClock, FixedClock>()
        .AddSingleton(Options.Create(new PandaScoreOptions()))
        .BuildServiceProvider();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 13, 23, 0, 0, TimeSpan.Zero);
    }
}
