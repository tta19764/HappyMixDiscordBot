using HappyMix.Bot.Services;
using HappyMix.Domain.CounterStrike;

namespace HappyMix.Application.Tests.Matches;

public sealed class DailyScheduleTests
{
    [Fact]
    public void LongSchedulePreservesAllMatchesInOrder()
    {
        var matches = Enumerable.Range(1, 300).Select(index => CreateMatch($"Match {index}")).ToArray();
        var embeds = DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(new DateOnly(2026, 9, 5), matches);

        Assert.True(embeds.Count > 1);
        Assert.All(embeds, embed => Assert.InRange(embed.Description.Length, 1, 4096));
        Assert.Equal(
            string.Join('\n', matches.Select(match => $"TBD — **{match.Name}** · League")),
            string.Join('\n', embeds.Select(embed => embed.Description)));
    }

    [Theory]
    [InlineData(4077, 1)]
    [InlineData(4078, 2)]
    [InlineData(9000, 3)]
    public void SingleLongEntryRespectsLimit(int nameLength, int expectedPages)
    {
        string name = new('a', nameLength);
        var embeds = DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(
            new DateOnly(2026, 9, 5), [CreateMatch(name)]);

        Assert.Equal(expectedPages, embeds.Count);
        Assert.All(embeds, embed => Assert.InRange(embed.Description.Length, 1, 4096));
        Assert.Equal($"TBD — **{name}** · League", string.Concat(embeds.Select(embed => embed.Description)));
    }

    [Fact]
    public void EmptyScheduleProducesOneMessage()
    {
        var embeds = DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(new DateOnly(2026, 9, 5), []);
        Assert.Equal("No tier 1 Counter-Strike 2 matches are scheduled today.", Assert.Single(embeds).Description);
    }

    [Fact]
    public void TournamentsHaveSeparatePagesEvenWithIdenticalNames()
    {
        var first = CreateMatch("First") with { TournamentId = 1 };
        var second = CreateMatch("Second") with { TournamentId = 2 };
        var third = CreateMatch("Third") with { TournamentId = 1 };
        var embeds = DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(
            new DateOnly(2026, 9, 5), [first, second, third]);

        Assert.Equal(2, embeds.Count);
        Assert.Contains("First", embeds[0].Description, StringComparison.Ordinal);
        Assert.Contains("Third", embeds[0].Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", embeds[0].Description, StringComparison.Ordinal);
        Assert.Contains("Second", embeds[1].Description, StringComparison.Ordinal);
        Assert.All(embeds, embed => Assert.Equal("League • Tournament", embed.Title));
    }

    private static CounterStrikeMatch CreateMatch(string name) =>
        new(1, name, null, "not_started", "League", "Tournament", [], [], [], null);
}
