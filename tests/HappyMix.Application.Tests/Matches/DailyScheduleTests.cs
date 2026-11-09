using HappyMix.Bot.Services;
using HappyMix.Domain.Esports;

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
        Assert.Equal("No followed main-team CS2 or Dota 2 matches are scheduled today.", Assert.Single(embeds).Description);
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
        Assert.All(embeds, embed => Assert.Equal("Counter-Strike 2 • League • Tournament", embed.Title));
    }

    [Fact]
    public void SameTournamentIdInDifferentDisciplinesHasSeparateLabelledPages()
    {
        var cs = CreateMatch("CS main team") with { TournamentId = 7 };
        var dota = CreateMatch("Dota main team") with { TournamentId = 7, Game = Esport.Dota2 };
        var embeds = DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(new DateOnly(2026, 9, 5), [cs, dota]);
        Assert.Equal(2, embeds.Count);
        Assert.StartsWith("Counter-Strike 2", embeds[0].Title, StringComparison.Ordinal);
        Assert.StartsWith("Dota 2", embeds[1].Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Esport.CounterStrike2, "Counter-Strike 2", "Map", "map")]
    [InlineData(Esport.Dota2, "Dota 2", "Game", "game")]
    public void IndividualResultUsesDisciplineSpecificTerminology(Esport discipline, string label, string title, string noun)
    {
        var match = CreateMatch("Main team") with
        {
            Game = discipline, Opponents = [new EsportsOpponent(3216, "NAVI")],
            Results = [new EsportsTeamResult(3216, 1)]
        };
        var embed = DiscordMatchAnnouncementPublisher.BuildGameResultEmbed(match,
            new EsportsGame(10, 1, "finished", true, 3216));
        Assert.Equal($"{label} • {title} 1 finished", embed.Title);
        Assert.Contains($"**NAVI** won the {noun}.", embed.Description, StringComparison.Ordinal);
    }

    private static EsportsMatch CreateMatch(string name) =>
        new(1, name, null, "not_started", "League", "Tournament", [], [], [], null);
}
