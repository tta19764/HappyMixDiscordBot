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
    public async Task WeeklyDayEditorReplacesTheWholeDaySoExistingRangesCanBeEdited()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        var day = new SetWeeklyDayCommand(1, 2, DayOfWeek.Monday,
            [new("19:00", "23:00"), new("12:00", "14:00", Tentative: true)], "UTC", "UTC");
        Assert.True((await sender.Send(day)).IsSuccess);
        Assert.True((await sender.Send(new SetWeeklyAvailabilityCommand(1, 2, DayOfWeek.Monday, "08", "09", "Europe/Kyiv", "UTC"))).IsSuccess);

        // Moving the start of an existing range used to be impossible because ranges are keyed by their start.
        Assert.True((await sender.Send(day with { Ranges = [new("18:00", "01:00"), new("12:30", "14:00", Tentative: true)] })).IsSuccess);

        using var reopened = CreateServices(await database.OpenAsync());
        sender = reopened.GetRequiredService<ISender>();
        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value;
        Assert.Equal(3, weekly.Count);
        Assert.Contains(new WeeklyAvailability(DayOfWeek.Monday, new(12, 30), new(14, 0), "UTC", true), weekly);
        Assert.Contains(new WeeklyAvailability(DayOfWeek.Monday, new(18, 0), new(1, 0), "UTC", false), weekly);
        Assert.Contains(weekly, range => range.TimeZone == "Europe/Kyiv" && range.Start == new TimeOnly(8, 0));

        Assert.True((await sender.Send(day with { Ranges = [] })).IsSuccess);
        Assert.Equal("Europe/Kyiv", Assert.Single((await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value).TimeZone);
    }

    [Fact]
    public async Task WeeklyDayEditorRejectsOverlapsAndKeepsTheSavedDay()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        Assert.True((await sender.Send(new SetWeeklyAvailabilityCommand(1, 2, DayOfWeek.Sunday, "23", "02", "UTC", "UTC"))).IsSuccess);
        var monday = new SetWeeklyDayCommand(1, 2, DayOfWeek.Monday, [new("10:00", "12:00")], "UTC", "UTC");
        Assert.True((await sender.Send(monday)).IsSuccess);

        Assert.True((await sender.Send(monday with { Ranges = [new("10:00", "12:00"), new("11:00", "13:00")] })).IsFailure);
        Assert.True((await sender.Send(monday with { Ranges = [new("22:00", "01:00"), new("23:30", "23:45")] })).IsFailure);
        Assert.True((await sender.Send(monday with { Ranges = [new("", "", FullDay: true), new("18:00", "19:00")] })).IsFailure);
        Assert.True((await sender.Send(monday with { Ranges = [new("01:00", "03:00")] })).IsFailure); // Sunday night spills over
        Assert.True((await sender.Send(monday with { Ranges = [new("21:00", "02:00")], Day = DayOfWeek.Saturday })).IsSuccess);
        Assert.True((await sender.Send(monday with { Day = DayOfWeek.Sunday, Ranges = [new("01:00", "03:00")] })).IsFailure); // Saturday night
        Assert.True((await sender.Send(monday with { Ranges = Enumerable.Range(0, 9).Select(hour => new WeeklyRangeInput($"{hour:00}:00", $"{hour:00}:30")).ToArray() })).IsFailure);

        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value;
        Assert.Equal(3, weekly.Count);
        Assert.Contains(new WeeklyAvailability(DayOfWeek.Monday, new(10, 0), new(12, 0), "UTC", false), weekly);
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

    [Fact]
    public async Task CopyingAWeeklyDayReplacesTheTargetDaysAndKeepsThemOnFailure()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        Assert.True((await sender.Send(new SetWeeklyDayCommand(1, 2, DayOfWeek.Monday,
            [new("18", "20"), new("21", "23", Tentative: true)], "UTC", "UTC"))).IsSuccess);
        Assert.True((await sender.Send(new SetWeeklyDayCommand(1, 2, DayOfWeek.Wednesday, [new("09", "11")], "UTC", "UTC"))).IsSuccess);

        var copied = await sender.Send(new CopyWeeklyDayCommand(1, 2, DayOfWeek.Monday, [DayOfWeek.Wednesday, DayOfWeek.Friday]));

        Assert.True(copied.IsSuccess, copied.Error.Description);
        Assert.Equal(2, copied.Value);
        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value;
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            Assert.Equal([(new TimeOnly(18, 0), false), (new TimeOnly(21, 0), true)],
                weekly.Where(item => item.Day == day).OrderBy(item => item.Start).Select(item => (item.Start, item.Tentative)).ToArray());

        // Thursday 22:00–08:00 can't be copied onto Wednesday: it would run into Thursday's morning. Wednesday stays.
        Assert.True((await sender.Send(new SetWeeklyDayCommand(1, 2, DayOfWeek.Tuesday, [new("22", "08")], "UTC", "UTC"))).IsSuccess);
        Assert.True((await sender.Send(new SetWeeklyDayCommand(1, 2, DayOfWeek.Thursday, [new("06", "09")], "UTC", "UTC"))).IsSuccess);
        Assert.True((await sender.Send(new CopyWeeklyDayCommand(1, 2, DayOfWeek.Tuesday, [DayOfWeek.Wednesday]))).IsFailure);
        Assert.Equal(2, (await sender.Send(new GetWeeklyAvailabilityQuery(1, 2))).Value.Count(item => item.Day == DayOfWeek.Wednesday));
        Assert.True((await sender.Send(new CopyWeeklyDayCommand(1, 2, DayOfWeek.Monday, [DayOfWeek.Monday]))).IsFailure);
    }

    [Fact]
    public async Task CopyingADateMovesItsWindowsAndReplacesTheTargetDate()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var settings = database.Services.GetRequiredService<IGuildSettingsStore>();
        await settings.SaveAsync(new GuildSettings(1, 0, "Europe/Kyiv"));
        using var services = CreateServices(database.Services);
        var sender = services.GetRequiredService<ISender>();
        var from = new DateOnly(2026, 11, 20);
        var to = new DateOnly(2026, 11, 24);
        Assert.True((await sender.Send(new SetDateAvailabilityCommand(1, 2, from, from, "18", "01", null, "UTC", false, true))).IsSuccess);
        Assert.True((await sender.Send(new SetDateAvailabilityCommand(1, 2, to, to, "10", "12", null, "UTC", false, false))).IsSuccess);

        var copied = await sender.Send(new CopyDateAvailabilityCommand(1, 2, from, to, "UTC"));

        Assert.True(copied.IsSuccess, copied.Error.Description);
        var windows = (await sender.Send(new GetUpcomingAvailabilityQuery(1, 2))).Value.OrderBy(item => item.Start).ToArray();
        Assert.Equal(2, windows.Length);
        // 24 Nov 18:00–01:00 in Kyiv (UTC+2), tentative like the original; the 10:00–12:00 window is gone.
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 16, 0, 0, TimeSpan.Zero), windows[1].Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 23, 0, 0, TimeSpan.Zero), windows[1].End);
        Assert.True(windows[1].Tentative);
        Assert.True((await sender.Send(new CopyDateAvailabilityCommand(1, 2, from, from, "UTC"))).IsFailure);
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
