using System.Net;
using System.Text;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Infrastructure;
using HappyMix.Infrastructure.PandaScore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Matches;

public sealed class PandaScoreClientTests
{
    [Theory]
    [InlineData("{\"id\":null,\"type\":null}", null)]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("{\"id\":42,\"type\":\"Team\"}", 42L)]
    public async Task GetMatchesForDayAsyncPreservesNullableGameWinner(string winner, long? expectedWinnerId)
    {
        string json = $$"""
            [{
              "id": 1,
              "name": "Team A vs Team B",
              "begin_at": "2026-09-05T12:00:00Z",
              "status": "running",
              "tournament": {"id": 7, "name": "Finals", "tier": "s"},
              "games": [{
                "id": 10,
                "position": 1,
                "status": "running",
                "complete": false,
                "winner": {{winner}}
              }]
            }]
            """;

        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new PandaScoreOptions
        {
            Token = "test-token",
            TimeZoneId = "UTC"
        }));
        services.ConfigureHttpClientDefaults(builder =>
            builder.ConfigurePrimaryHttpMessageHandler(() => new StubHandler(json)));
        using ServiceProvider provider = services.BuildServiceProvider();

        var day = await provider.GetRequiredService<IPandaScoreClient>()
            .GetMatchesForDayAsync(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);

        var match = Assert.Single(day.Matches);
        var game = Assert.Single(match.Games);
        Assert.Equal(expectedWinnerId, game.WinnerId);
    }

    [Theory]
    [InlineData("s", true)]
    [InlineData("S", true)]
    [InlineData("a", true)]
    [InlineData("A", true)]
    [InlineData("b", false)]
    [InlineData("c", false)]
    [InlineData("d", false)]
    [InlineData(null, false)]
    public async Task OnlyConfiguredTierTournamentsAreReturned(string? tier, bool included)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { id = 1, name = "Match", status = "running", tournament = new { id = 7, name = "Finals", tier } }
        });
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new PandaScoreOptions
        {
            Token = "test-token", TimeZoneId = "UTC"
        }));
        services.ConfigureHttpClientDefaults(builder =>
            builder.ConfigurePrimaryHttpMessageHandler(() => new StubHandler(json)));
        using ServiceProvider provider = services.BuildServiceProvider();
        var day = await provider.GetRequiredService<IPandaScoreClient>().GetMatchesForDayAsync(
            new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(included ? 1 : 0, day.Matches.Count);
        if (included)
        {
            Assert.Equal(7L, Assert.Single(day.Matches).TournamentId);
        }
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
