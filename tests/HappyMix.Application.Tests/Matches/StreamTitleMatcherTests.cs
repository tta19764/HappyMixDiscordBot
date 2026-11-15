using HappyMix.Bot.Services;
using HappyMix.Domain.Esports;

namespace HappyMix.Application.Tests.Matches;

public sealed class StreamTitleMatcherTests
{
    private static readonly EsportsOpponent Navi = new(1, "Natus Vincere", "NAVI");
    private static readonly EsportsOpponent Spirit = new(2, "Team Spirit", "TS");
    private static readonly EsportsOpponent G2 = new(3, "G2 Esports", "G2");
    private static readonly EsportsOpponent Faze = new(4, "FaZe Clan", "FaZe");
    private static readonly EsportsOpponent VirtusPro = new(5, "Virtus.pro", "VP");

    [Theory]
    [InlineData("NAVI vs Team Spirit | IEM Katowice Playoffs")]
    [InlineData("🔴 Na'Vi — Spirit | BO3 | !drops")]
    [InlineData("natus vincere x TEAM SPIRIT грандфинал")]
    [InlineData("[RU] NaVi vs TeamSpirit")]
    public void TitlesNamingBothTeamsMatch(string title) =>
        Assert.True(new StreamTitleMatcher([Navi, Spirit]).Matches(title));

    [Theory]
    [InlineData("NAVI vs FaZe | IEM Katowice")]          // another match on the same channel
    [InlineData("Spirit warmup, then ranked")]           // only one team
    [InlineData("Navigation tutorial with Spirit fans")] // "navi" inside a longer word
    [InlineData("")]
    [InlineData(null)]
    public void TitlesMissingATeamDoNotMatch(string? title) =>
        Assert.False(new StreamTitleMatcher([Navi, Spirit]).Matches(title));

    [Fact]
    public void FillerWordsAndPunctuationAreOptional()
    {
        var matcher = new StreamTitleMatcher([G2, Faze]);
        Assert.True(matcher.Matches("G2 vs FAZE - BLAST Premier"));
        Assert.True(matcher.Matches("g2 esports – faze clan"));
        Assert.False(matcher.Matches("G2A sale stream with FaZe"));
        Assert.True(new StreamTitleMatcher([VirtusPro, G2]).Matches("Virtus Pro vs G2"));
        Assert.True(new StreamTitleMatcher([VirtusPro, G2]).Matches("VP — G2"));
        Assert.True(new StreamTitleMatcher([new EsportsOpponent(6, "Ninjas in Pyjamas", "NIP"), G2]).Matches("NiP vs G2"));
    }

    // Real announcement: "FaZe vs fnatic · PGL · Playoffs", linked to cs2_maincast, petrovich_cast and leniniw.
    [Theory]
    [InlineData("FaZe", null)]
    [InlineData("FaZe Clan", "FaZe")]
    public void PglFazeFnaticStreamTitleMatches(string fazeName, string? fazeAcronym)
    {
        var matcher = new StreamTitleMatcher([new EsportsOpponent(10, fazeName, fazeAcronym), new EsportsOpponent(11, "fnatic", "FNC")]);
        Assert.True(matcher.Matches("fnatic vs FaZe [1:0] BO3 - PGL Cluj-Napoca 2027 CQ"));
        Assert.False(matcher.Matches("PGL Cluj-Napoca 2027 CQ — Day 3 | !prime"));
        Assert.False(matcher.Matches("fnatic vs NAVI [0:0] BO3 - PGL Cluj-Napoca 2027 CQ"));
    }

    [Fact]
    public void AccentsAreIgnoredAndNoTeamsMeansAnyTitle()
    {
        Assert.True(new StreamTitleMatcher([new EsportsOpponent(7, "Heroic", null), new EsportsOpponent(8, "Astralis", "AST")])
            .Matches("HÉROÏC vs Astralis"));
        Assert.True(new StreamTitleMatcher([]).Matches("anything"));
        Assert.Equal(["team spirit", "spirit", "ts"], StreamTitleMatcher.Variants(Spirit));
    }
}
