using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Bot.Services;

namespace HappyMix.Application.Tests.Matches;

public sealed class StreamDiscoveryTests
{
    [Fact]
    public void EmptyScheduleKeepsItsLocalizedNotification()
    {
        var embeds = DiscordMatchAnnouncementPublisher.BuildDailyScheduleEmbeds(new DateOnly(2026, 11, 17), [], language: "uk");
        Assert.Equal(NotificationText.Get("uk", "Notify_NoMatches"), Assert.Single(embeds).Description);
    }

    [Fact]
    public void EnabledCriteriaAndTheirOrderChangeRanking()
    {
        TwitchLiveStreams.Stream[] streams = [new("english", "live", "A vs B", "en", ["Poland"], 900),
            new("ukrainian", "live", "A vs B", "uk", ["Ukraine"], 20)];
        Assert.Equal("ukrainian", TwitchLiveStreams.Rank(streams, new(true, "uk,en", "Poland", "", "language,viewers")).First().User_Login);
        Assert.Equal("english", TwitchLiveStreams.Rank(streams, new(true, "uk,en", "Poland", "", "country,language")).First().User_Login);
        Assert.Equal("english", TwitchLiveStreams.Rank(streams, new(true, "uk,en", "Poland", "", "viewers")).First().User_Login);
    }

    [Theory]
    [InlineData("language,language")]
    [InlineData("country,unknown")]
    public void RejectsAmbiguousOrUnsupportedCriteria(string priority) => Assert.False(new StreamDiscoveryPreferences(Priority: priority).IsValid);

    [Theory]
    [InlineData("en", "Match started")]
    [InlineData("uk", "Матч розпочався")]
    [InlineData("ru", "Матч начался")]
    public void NotificationLanguageIsExplicitAndIndependentOfThreadCulture(string language, string expected) =>
        Assert.Equal(expected, NotificationText.Get(language, "Notify_Started"));
}
