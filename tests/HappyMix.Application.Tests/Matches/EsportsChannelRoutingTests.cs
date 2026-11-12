using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Bot.Jobs;
using HappyMix.Domain.Esports;

namespace HappyMix.Application.Tests.Matches;

public sealed class EsportsChannelRoutingTests
{
    [Fact]
    public void GamesSharingAChannelUseOneRoute()
    {
        var settings = new GuildSettings(1, 20, "UTC", new GuildEsportsPreferences(CounterStrikeChannelId: 20));

        var route = Assert.Single(SynchronizeMatchesJob.GetRoutes(settings));

        Assert.Equal(20UL, route.Key);
        Assert.Equal([Esport.CounterStrike2, Esport.Dota2], route.ToArray());
    }

    [Theory]
    [InlineData(20UL, 20UL)]
    [InlineData(0UL, 0UL)]
    public void Cs2OverrideRoutesSeparatelyFromTheDotaFallback(ulong defaultChannel, ulong dotaChannel)
    {
        var settings = new GuildSettings(1, defaultChannel, "UTC",
            new GuildEsportsPreferences(CounterStrikeChannelId: 30));
        var routes = SynchronizeMatchesJob.GetRoutes(settings);

        Assert.Equal(Esport.CounterStrike2, Assert.Single(routes.Single(route => route.Key == 30)));
        if (dotaChannel == 0)
            Assert.Single(routes);
        else
            Assert.Equal(Esport.Dota2, Assert.Single(routes.Single(route => route.Key == dotaChannel)));
    }
}
