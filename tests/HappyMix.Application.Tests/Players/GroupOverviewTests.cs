using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Bot.Interactions;
using HappyMix.Bot.Web;

namespace HappyMix.Application.Tests.Players;

/// <summary>The group Overview's next event (who's going) and the Dota 2 line of <c>/stats</c>.</summary>
public sealed class GroupOverviewTests
{
    [Fact]
    public void AnEventRowListsWhoIsGoingToThatDate()
    {
        var id = Guid.NewGuid();
        var date = new DateOnly(2026, 11, 27);
        var start = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        var at = DateTimeOffset.UnixEpoch;
        var guildEvent = new GuildEvent(id, 1, 10, null, "Scrims", null, "UTC", date, new TimeOnly(18, 0), 180, EventRecurrence.Once, [], null, at, null);
        var details = new EventDetails(guildEvent, [20, 30, 40, 50], [new EventOccurrence(id, date, start, start.AddHours(3), OccurrenceStatus.Pending, null, null)],
            [
                new EventResponse(id, date, 30, true, false, at.AddMinutes(2)),
                new EventResponse(id, date, 20, true, false, at.AddMinutes(1)),
                new EventResponse(id, date, 40, false, true, at.AddMinutes(3)), // said yes, then withdrew
                new EventResponse(id, date, 50, false, false, at.AddMinutes(4)),
                new EventResponse(id, date.AddDays(7), 50, true, false, at.AddMinutes(5)) // another date
            ], []);

        var row = EventText.Row(details, details.Occurrences[0], 20, TimeZoneInfo.Utc, null, user => "#" + user, 1);
        Assert.Equal([20UL, 30UL], row.Going);
        Assert.True(row.MyAnswer);
    }

    [Fact]
    public void StatsTextAddsFormRoleAndMmr()
    {
        var profile = new DotaProfile(1, "kai", 45, null, 10, 5, [])
        {
            Recent = new DotaRecent(20, 13, "WWWLWLLWWLWWLWLWWLWW", 3, 7, 5, 11, 500, 600, 8, 5, 12, 8, 10, 7),
            Roles = [new DotaRole("2", 21, 13), new DotaRole("3", 9, 4)]
        };
        Assert.Equal("last 20 13W 7L, 3 wins in a row · mostly pos 2 mid (70%) · ≈ +100 MMR over 10 ranked", PlayerGamesModule.DotaMore(profile));
        Assert.Equal("", PlayerGamesModule.DotaMore(new DotaProfile(1, null, null, null, 0, 0, [])));
    }
}
