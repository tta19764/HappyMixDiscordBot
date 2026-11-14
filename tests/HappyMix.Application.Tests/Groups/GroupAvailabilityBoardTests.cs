using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Application.Tests.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Groups;

public sealed class GroupAvailabilityBoardTests
{
    private const ulong Guild = 1;
    private const ulong Organizer = 10;
    private const ulong Member = 20;
    private const ulong Quiet = 21;
    private const ulong Invitee = 55;
    private const ulong Outsider = 99;
    private const ulong Moderator = 77;
    private const string Kyiv = "Europe/Kyiv";

    // Saturday 24 October 2026, 12:00 in Kyiv (UTC+3). Kyiv falls back to UTC+2 at 04:00 on Sunday 25 October.
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Saturday = new(2026, 10, 24);
    private static readonly DateOnly Sunday = new(2026, 10, 25);
    private static readonly DateOnly Monday = new(2026, 10, 26);

    [Fact]
    public async Task OnlyPeopleWhoCanSeeTheRosterCanLoadTheBoard()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();

        Assert.True((await fixture.Board(Member, group)).IsSuccess);
        Assert.True((await fixture.Board(Organizer, group)).IsSuccess);
        Assert.True((await fixture.Board(Moderator, group, moderate: true)).IsSuccess);

        // Invitees see the overview only; outsiders and other servers see nothing, even with a valid ID.
        foreach (var result in new[]
        {
            await fixture.Board(Invitee, group),
            await fixture.Board(Outsider, group),
            await fixture.Sender.Send(new GetGroupAvailabilityBoardQuery(new GroupActor(2, Moderator, true), group, null, Kyiv)),
            await fixture.Board(Member, Guid.NewGuid())
        })
        {
            Assert.True(result.IsFailure);
            Assert.Equal("Group.NotFound", result.Error.Code);
        }
    }

    [Fact]
    public async Task EveryMemberGetsARowAndMembersWithoutDataComeLast()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();
        await fixture.Availability.SaveWeeklyAsync(Guild, Member, new WeeklyAvailability(DayOfWeek.Saturday, new(18, 0), new(22, 0), Kyiv));
        // Availability of someone outside the group never reaches the board.
        await fixture.Availability.SaveWeeklyAsync(Guild, Outsider, new WeeklyAvailability(DayOfWeek.Saturday, new(18, 0), new(22, 0), Kyiv));

        var board = (await fixture.Board(Organizer, group)).Value;

        Assert.Equal([Member, Organizer, Quiet], board.Rows.Select(row => row.UserId));
        Assert.True(board.Rows[0].HasData);
        Assert.All(board.Rows.Skip(1), row => Assert.Empty(row.Ranges));
        Assert.Equal(Kyiv, board.TimeZone);
        Assert.Equal(Saturday, board.Today);
        Assert.Equal(Saturday, board.Date);
        Assert.Equal(Enumerable.Range(0, 7).Select(Saturday.AddDays), board.Days);
    }

    [Fact]
    public async Task DatesOutsideTheSevenDayWindowFallBackToToday()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();

        Assert.Equal(Monday, (await fixture.Board(Member, group, date: Monday)).Value.Date);
        Assert.Equal(Saturday, (await fixture.Board(Member, group, date: Saturday.AddDays(7))).Value.Date);
        Assert.Equal(Saturday, (await fixture.Board(Member, group, date: Saturday.AddDays(-1))).Value.Date);
    }

    [Fact]
    public async Task SchedulesAreConvertedToTheBoardZoneBeforeChoosingTheDate()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();
        // Sunday 20:00–23:00 in New York is Monday 02:00–05:00 in Kyiv after the fall-back.
        await fixture.Availability.SaveWeeklyAsync(Guild, Member,
            new WeeklyAvailability(DayOfWeek.Sunday, new(20, 0), new(23, 0), "America/New_York"));

        var sunday = (await fixture.Board(Member, group, date: Sunday)).Value;
        var monday = (await fixture.Board(Member, group, date: Monday)).Value;

        Assert.False(Row(sunday, Member).HasData);
        var range = Assert.Single(Row(monday, Member).Ranges);
        Assert.Equal((new TimeOnly(2, 0), new TimeOnly(5, 0), AvailabilitySource.Weekly), (range.Start, range.End, range.Source));
        Assert.False(range.FromPreviousDay || range.IntoNextDay || range.AllDay);
    }

    [Fact]
    public async Task OvernightRangesSplitAcrossMidnightAndKeepMultipleRanges()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();
        await fixture.Availability.SaveWeeklyAsync(Guild, Member, new WeeklyAvailability(DayOfWeek.Saturday, new(21, 0), new(1, 30), Kyiv));
        await fixture.Availability.SaveWeeklyAsync(Guild, Member, new WeeklyAvailability(DayOfWeek.Sunday, new(10, 0), new(12, 0), Kyiv));
        // A one-off window on Sunday afternoon (UTC+2 after the fall-back).
        await fixture.Availability.SaveAsync(Guild, Member, null, new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.FromHours(2)));

        var saturday = Assert.Single(Row((await fixture.Board(Member, group)).Value, Member).Ranges);
        Assert.Equal((new TimeOnly(21, 0), new TimeOnly(0, 0)), (saturday.Start, saturday.End));
        Assert.True(saturday.IntoNextDay);
        Assert.False(saturday.FromPreviousDay);

        var sunday = Row((await fixture.Board(Member, group, date: Sunday)).Value, Member).Ranges;
        Assert.Equal(3, sunday.Length);
        Assert.Equal((new TimeOnly(0, 0), new TimeOnly(1, 30), true), (sunday[0].Start, sunday[0].End, sunday[0].FromPreviousDay));
        Assert.Equal((new TimeOnly(10, 0), new TimeOnly(12, 0), AvailabilitySource.Weekly), (sunday[1].Start, sunday[1].End, sunday[1].Source));
        Assert.Equal((new TimeOnly(15, 0), new TimeOnly(17, 0), AvailabilitySource.OneOff), (sunday[2].Start, sunday[2].End, sunday[2].Source));
    }

    [Fact]
    public async Task FreeTogetherCountsEachHourAndNowIsOnlyShownForToday()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();
        await fixture.Availability.SaveWeeklyAsync(Guild, Member, new WeeklyAvailability(DayOfWeek.Saturday, new(18, 30), new(22, 0), Kyiv));
        await fixture.Availability.SaveWeeklyAsync(Guild, Organizer, new WeeklyAvailability(DayOfWeek.Saturday, new(20, 0), new(1, 0), Kyiv));

        var today = (await fixture.Board(Member, group)).Value;

        Assert.Equal(24, today.FreeByHour.Length);
        Assert.Equal(0, today.FreeByHour[17]);
        Assert.Equal(1, today.FreeByHour[18]); // 18:30 counts for the 18:00 hour
        Assert.Equal(2, today.FreeByHour[20]);
        Assert.Equal(2, today.FreeByHour[21]);
        Assert.Equal(1, today.FreeByHour[22]); // Member ends exactly at 22:00
        Assert.Equal(1, today.FreeByHour[23]); // Organizer continues past midnight
        Assert.Equal(new TimeOnly(12, 0), today.Now);

        var sunday = (await fixture.Board(Member, group, date: Sunday)).Value;
        Assert.Null(sunday.Now);
        Assert.Equal(1, sunday.FreeByHour[0]); // carried over until 01:00
        Assert.Equal(0, sunday.FreeByHour[1]);
    }

    [Fact]
    public async Task RecurringFullDayStaysOnLocalMidnightAcrossDaylightSaving()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();
        var saved = await fixture.Sender.Send(new SetWeeklyAvailabilityCommand(Guild, Member, DayOfWeek.Sunday, "", "", null, Kyiv, FullDay: true));
        Assert.True(saved.IsSuccess);
        Assert.True(saved.Value.IsFullDay);

        // Sunday 25 October is 25 hours long in Kyiv; it is still exactly one day on the board.
        var sunday = Assert.Single(Row((await fixture.Board(Member, group, date: Sunday)).Value, Member).Ranges);
        Assert.True(sunday.AllDay);
        Assert.False(sunday.FromPreviousDay || sunday.IntoNextDay);
        Assert.False(Row((await fixture.Board(Member, group)).Value, Member).HasData);
        Assert.False(Row((await fixture.Board(Member, group, date: Monday)).Value, Member).HasData);

        var occurrence = GroupAvailabilityBoardQueries.Occurrences(saved.Value, Sunday).Single(item => item.Start.Day == 25);
        Assert.Equal(TimeSpan.FromHours(25), occurrence.End - occurrence.Start);
    }

    [Fact]
    public async Task SavingFreeAllDayAddsOneWindowWithoutChangingOtherSchedules()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid group = await fixture.SeedAsync();
        var weekly = new WeeklyAvailability(DayOfWeek.Monday, new(18, 0), new(0, 30), Kyiv);
        await fixture.Availability.SaveWeeklyAsync(Guild, Member, weekly);
        var later = await fixture.Availability.SaveAsync(Guild, Member, null,
            new DateTimeOffset(2026, 10, 27, 19, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 10, 27, 21, 0, 0, TimeSpan.FromHours(2)));

        var result = await fixture.Sender.Send(new SetTodayAvailabilityCommand(Guild, Member, "", "", null, Kyiv, FullDay: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.FromHours(3)), result.Value.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(3)), result.Value.End);
        Assert.Equal([weekly], await fixture.Availability.ListWeeklyAsync(Guild, Member));
        Assert.Equal([result.Value.Id, later.Id], (await fixture.Availability.ListAsync(Guild, Member)).Select(window => window.Id));

        var today = Assert.Single(Row((await fixture.Board(Member, group)).Value, Member).Ranges);
        Assert.Equal((true, AvailabilitySource.OneOff), (today.AllDay, today.Source));
        Assert.False(Row((await fixture.Board(Member, group, date: Sunday)).Value, Member).HasData);

        // An ordinary late range keeps its meaning: 18:00 today until 00:30 tomorrow.
        var ordinary = await fixture.Sender.Send(new SetTodayAvailabilityCommand(Guild, Quiet, "18:00", "00:30", null, Kyiv));
        Assert.Equal(TimeSpan.FromHours(6.5), ordinary.Value.End - ordinary.Value.Start);
    }

    [Fact]
    public async Task WeeklyFullDayReplacesOnlyThatDay()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var friday = new WeeklyAvailability(DayOfWeek.Friday, new(20, 0), new(23, 0), Kyiv);
        await fixture.Availability.SaveWeeklyAsync(Guild, Member, friday);
        await fixture.Availability.SaveWeeklyAsync(Guild, Member, new WeeklyAvailability(DayOfWeek.Sunday, new(10, 0), new(12, 0), Kyiv));

        Assert.True((await fixture.Sender.Send(new SetWeeklyAvailabilityCommand(Guild, Member, DayOfWeek.Sunday, "", "", null, Kyiv, true))).IsSuccess);

        var saved = await fixture.Availability.ListWeeklyAsync(Guild, Member);
        Assert.Equal(friday, saved.Single(item => item.Day == DayOfWeek.Friday));
        Assert.True(saved.Single(item => item.Day == DayOfWeek.Sunday).IsFullDay);
        Assert.True((await fixture.Sender.Send(new SetWeeklyAvailabilityCommand(Guild, Member, DayOfWeek.Monday, "18:00", "18:00", null, Kyiv))).IsFailure);
    }

    private static BoardRow Row(GroupAvailabilityBoard board, ulong user) => board.Rows.Single(row => row.UserId == user);

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;

        public IGuildGroupStore Groups => database.Services.GetRequiredService<IGuildGroupStore>();
        public IAvailabilityStore Availability => database.Services.GetRequiredService<IAvailabilityStore>();
        public ISender Sender => application.GetRequiredService<ISender>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var settings = fixture.database.Services.GetRequiredService<IGuildSettingsStore>();
            await settings.UpdateAsync(Guild, Kyiv, saved => saved);
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(fixture.Groups).AddSingleton(settings).AddSingleton(fixture.Availability)
                .AddSingleton<IClock>(new FixedClock(Now))
                .AddSingleton<IGroupRoleGateway, NoRoles>()
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        /// <summary>One group: organizer 10, members 20 and 21, invitation for 55.</summary>
        public async Task<Guid> SeedAsync()
        {
            await Groups.UpdateAsync(Guild, "Friday", _ => new GuildGroup("Friday", Organizer, [Organizer, Member, Quiet], [Invitee]));
            return (await Groups.ListAsync(Guild)).Single().Id;
        }

        public Task<HappyMix.Domain.Abstractions.Result<GroupAvailabilityBoard>> Board(ulong user, Guid group,
            bool moderate = false, DateOnly? date = null) =>
            Sender.Send(new GetGroupAvailabilityBoardQuery(new GroupActor(Guild, user, moderate), group, date, Kyiv));

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class NoRoles : IGroupRoleGateway
    {
        public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) => Task.FromResult<IReadOnlyCollection<ulong>>([]);
        public Task<ulong> CreateAsync(ulong guildId, string name) => Task.FromResult(1UL);
        public Task DeleteAsync(ulong guildId, ulong roleId) => Task.CompletedTask;
        public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta) =>
            Task.CompletedTask;
    }
}
