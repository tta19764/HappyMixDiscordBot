using HappyMix.Bot.Services;
using HappyMix.Domain.Esports;

namespace HappyMix.Application.Tests.Matches;

public sealed class DailyScheduleTests
{
    [Fact]
    public void ScheduleLinksToThePreferredBroadcastListedForTheMatch()
    {
        var match = CreateMatch("FUT vs MOUZ") with
        {
            StreamUrls = ["https://www.twitch.tv/cs2_maincast2?ref=fixture", "https://www.twitch.tv/eslcs"]
        };
        var embeds = DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(
            new DateOnly(2026, 11, 12), [match],
            (fixture, language) => TwitchChannels.Format(fixture, ["cs2_maincast", "cs2_maincast2"], language));

        string description = Assert.Single(embeds).Description;
        Assert.Contains("Watch: [cs2_maincast2](https://www.twitch.tv/cs2_maincast2)", description, StringComparison.Ordinal);
        Assert.DoesNotContain("eslcs", description, StringComparison.Ordinal);
        Assert.DoesNotContain("[cs2_maincast]", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleLabelsFallbackChannelsAndOmitsLinksWhenDisabled()
    {
        var match = CreateMatch("NAVI vs Monte") with { Game = Esport.Dota2 };
        Assert.Equal("\nTwitch channels: [ghostik](https://www.twitch.tv/ghostik)",
            TwitchChannels.Format(match, ["https://twitch.tv/Ghostik", "ghostik", "https://example.com/ghostik"]));
        Assert.Empty(TwitchChannels.Format(match, []));
        Assert.Empty(TwitchChannels.Format(match with { Status = "finished" }, ["ghostik"]));
    }

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
        Assert.Equal("Followed teams · CS2 / Dota 2", embeds[0].Title);
    }

    [Fact]
    public void EmptyScheduleForOneGameNamesOnlyThatGameInItsLanguage()
    {
        var embed = Assert.Single(DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(new DateOnly(2026, 9, 5), [],
            language: "uk", games: [Esport.Dota2]));
        Assert.Equal("Відстежувані команди · Dota 2", embed.Title);
        Assert.Equal("Сьогодні немає запланованих матчів відстежуваних команд Dota 2.", embed.Description);
        Assert.StartsWith("2026-09-05 • Основні команди", embed.Footer!.Value.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "Counter-Strike 2 • Map 1 finished", "Watch")]
    [InlineData("uk", "Counter-Strike 2 • Мапа 1 завершилася", "Дивитися")]
    public void ResultTitlesAndScheduleLinksFollowTheGameLanguage(string language, string title, string watch)
    {
        var match = CreateMatch("Main team") with
        {
            Opponents = [new EsportsOpponent(3216, "NAVI")], Results = [new EsportsTeamResult(3216, 1)],
            StreamUrls = ["https://www.twitch.tv/cs2_maincast"]
        };
        Assert.Equal(title, DiscordMatchAnnouncementPublisher.BuildGameResultEmbed(match, new EsportsGame(10, 1, "finished", true, 3216), language).Title);
        Assert.StartsWith($"\n{watch}: ", TwitchChannels.Format(match, ["cs2_maincast"], language), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveredBroadcastsGetTheirOwnLocalizedLine()
    {
        Assert.Equal("\nНаживо на Twitch: [a](https://www.twitch.tv/a)\nІнші прямі трансляції: [b](https://www.twitch.tv/b)",
            DiscordMatchAnnouncementPublisher.StreamLines("uk", ["a"], ["b"]));
        Assert.Equal("\nMore live broadcasts: [b](https://www.twitch.tv/b)", DiscordMatchAnnouncementPublisher.StreamLines("en", [], ["b"]));
        Assert.Empty(DiscordMatchAnnouncementPublisher.StreamLines("en", [], []));
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
