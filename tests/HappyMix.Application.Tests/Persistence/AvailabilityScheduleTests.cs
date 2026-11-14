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
using Microsoft.Extensions.Configuration;
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
    public async Task WeeklyAvailabilityAddsRangesAndUpdatesMatchingStartAndZone()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        var command = new SetWeeklyAvailabilityCommand(1, 2, DayOfWeek.Friday, "17", "00:30", "-05:00", "UTC");
        Assert.True((await sender.Send(command)).IsSuccess);
        Assert.True((await sender.Send(command with { End = "01:00", Tentative = true })).IsSuccess);

        using var reopened = CreateServices(await database.OpenAsync());
        sender = reopened.GetRequiredService<ISender>();
        var saved = Assert.Single((await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value);
        Assert.Equal(new WeeklyAvailability(DayOfWeek.Friday, new TimeOnly(17, 0), new TimeOnly(1, 0), "UTC-05:00", true), saved);
        Assert.Empty((await sender.Send(new GetWeeklyAvailabilityQuery(1, 3))).Value);
        Assert.True((await sender.Send(new ClearWeeklyAvailabilityCommand(1, 2, DayOfWeek.Friday))).IsSuccess);
        Assert.Empty((await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value);
    }

    [Fact]
    public async Task MultipleTodayAndWeeklyRangesKeepCertaintyAndCanBeRemovedIndividually()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        Assert.True((await sender.Send(new SetTodayAvailabilityCommand(1, 2, "17", "19", "Europe/Kyiv", "UTC"))).IsSuccess);
        Assert.True((await sender.Send(new SetTodayAvailabilityCommand(1, 2, "21", "00:30", "Europe/Kyiv", "UTC", Tentative: true))).IsSuccess);
        var today = (await sender.Send(new GetUpcomingAvailabilityQuery(1, 2))).Value;
        Assert.Equal(2, today.Length);
        Assert.False(today[0].Tentative);
        Assert.True(today[1].Tentative);
        Assert.True((await sender.Send(new RemoveAvailabilityCommand(1, 2, today[0].Id))).IsSuccess);
        Assert.True(Assert.Single((await sender.Send(new GetUpcomingAvailabilityQuery(1, 2))).Value).Tentative);

        var morning = new SetWeeklyAvailabilityCommand(1, 2, DayOfWeek.Monday, "10", "12", "UTC", "UTC");
        Assert.True((await sender.Send(morning)).IsSuccess);
        Assert.True((await sender.Send(morning with { Start = "18", End = "00:30", Tentative = true })).IsSuccess);
        using var reopened = CreateServices(await database.OpenAsync());
        sender = reopened.GetRequiredService<ISender>();
        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value;
        Assert.Equal(2, weekly.Count);
        Assert.True(weekly[1].Tentative);
        Assert.True((await sender.Send(new ClearWeeklyAvailabilityCommand(1, 3, DayOfWeek.Monday, new(10, 0), "UTC"))).IsFailure);
        Assert.True((await sender.Send(new ClearWeeklyAvailabilityCommand(1, 2, DayOfWeek.Monday, new(10, 0), "UTC"))).IsSuccess);
        Assert.True(Assert.Single((await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value).Tentative);
        Assert.True((await sender.Send(new ClearWeeklyAvailabilityCommand(1, 2, DayOfWeek.Monday))).IsSuccess);
        Assert.Empty((await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value);
    }

    [Fact]
    public async Task WeeklyOverlapIncludingSundayRolloverIsRejectedWithoutLosingRanges()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        var night = new SetWeeklyAvailabilityCommand(1, 2, DayOfWeek.Sunday, "23", "02", "UTC", "UTC");
        Assert.True((await sender.Send(night)).IsSuccess);
        Assert.True((await sender.Send(night with { Day = DayOfWeek.Monday, Start = "01", End = "03" })).IsFailure);
        Assert.True((await sender.Send(night with { Day = DayOfWeek.Monday, Start = "02", End = "03", Tentative = true })).IsSuccess);
        Assert.True((await sender.Send(night with { Day = DayOfWeek.Monday, FullDay = true })).IsFailure);
        Assert.Equal(2, (await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value.Count);
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
        Assert.Contains(CommandHelp.Read(interactions), command => command.Path == "availability set today" &&
            command.Options.Any(option => option.Name == "all_day" && !option.IsRequired));
        Assert.Equal(["timetable", "today"], set.SlashCommands.Select(command => command.Name).Order());
    }

    private static ServiceProvider CreateServices(IServiceProvider storage) => new ServiceCollection()
        .AddLogging().AddApplication()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
        .AddSingleton(storage.GetRequiredService<IAvailabilityStore>())
        .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
        .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
        .AddSingleton<IClock, FixedClock>()
        .AddSingleton(Options.Create(new PandaScoreOptions()))
        .BuildServiceProvider();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 13, 23, 0, 0, TimeSpan.Zero);
    }
}
