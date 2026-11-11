using System.Net;
using System.Text;
using System.Text.Json;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Domain.Esports;
using HappyMix.Infrastructure;
using HappyMix.Infrastructure.PandaScore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HappyMix.Application.Tests.Matches;

public sealed class PandaScoreClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3216, "csgo", Esport.CounterStrike2)]
    [InlineData(136955, "csgo", Esport.CounterStrike2)]
    [InlineData(131441, "csgo", Esport.CounterStrike2)]
    [InlineData(129381, "csgo", Esport.CounterStrike2)]
    [InlineData(136497, "csgo", Esport.CounterStrike2)]
    [InlineData(3210, "csgo", Esport.CounterStrike2)]
    [InlineData(1699, "dota2", Esport.Dota2)]
    [InlineData(132557, "dota2", Esport.Dota2)]
    public async Task DefaultConfigurationIncludesVerifiedMainTeams(long team, string path, Esport game)
    {
        var handler = new StubHandler(uri => Is(uri, path, "running") ? Json(Match(1, team)) : "[]");
        using var provider = Build(handler);
        var day = await Load(provider);
        Assert.Equal(game, Assert.Single(day.Matches).Game);
    }

    [Theory]
    [InlineData(137462)]
    [InlineData(138023)]
    [InlineData(137202)]
    [InlineData(133943)]
    [InlineData(135199)]
    public async Task AcademyAndSimilarNamesAreNotFollowed(long team)
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "running")
            ? Json(Match(1, team, name: "Natus Vincere")) : "[]"));
        Assert.Empty((await Load(provider)).Matches);
    }

    [Fact]
    public async Task MainTeamVsJuniorIsIncludedButJuniorAloneIsNot()
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "running")
            ? Json(Match(1, 3216, opponent: 133943), Match(2, 133943)) : "[]"));
        Assert.Equal(1, Assert.Single((await Load(provider)).Matches).Id);
    }

    [Fact]
    public async Task TeamIdsAreScopedToTheirDiscipline()
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "dota2", "running")
            ? Json(Match(1, 3216), Match(2, 136955)) : "[]"));
        Assert.Empty((await Load(provider)).Matches);
    }

    [Theory]
    [InlineData("{\"id\":null,\"type\":null}", null)]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("{\"id\":42,\"type\":\"Team\"}", 42L)]
    public async Task PreservesNullableGameWinner(string winner, long? expected)
    {
        var fixture = Json(Match(1, 3216));
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "running")
            ? fixture.Replace("\"games\":[]", "\"games\":[{\"id\":10,\"position\":1,\"status\":\"running\",\"complete\":false,\"winner\":" + winner + "}]", StringComparison.Ordinal)
            : "[]"));
        var match = Assert.Single((await Load(provider)).Matches);
        Assert.Equal(expected, Assert.Single(match.Games).WinnerId);
    }

    [Theory]
    [InlineData("s", true)]
    [InlineData("A", true)]
    [InlineData("b", false)]
    [InlineData(null, false)]
    public async Task OptionalTierRestrictionStillWorks(string? tier, bool included)
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "running")
            ? Json(Match(1, 3216, tier: tier)) : "[]"), new PandaScoreOptions
            { Token = "test", TimeZoneId = "UTC", IncludedTournamentTiers = ["s", "a"] });
        Assert.Equal(included ? 1 : 0, (await Load(provider)).Matches.Count);
    }

    [Fact]
    public async Task AllTiersIncludingUnknownAreIncludedByDefault()
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "running")
            ? Json(Match(1, 3216, tier: "b"), Match(2, 3216, tier: null)) : "[]"));
        Assert.Equal(2, (await Load(provider)).Matches.Count);
    }

    [Fact]
    public async Task PaginatesAndKeepsSameIdsInDifferentDisciplinesSeparate()
    {
        var handler = new StubHandler(uri =>
        {
            if (Is(uri, "csgo", "running"))
            {
                return uri.Query.Split('&').Contains("page=1", StringComparer.Ordinal)
                    ? Json(Enumerable.Range(1, 100).Select(id => Match(id, 3216)).ToArray())
                    : Json(Match(101, 131441));
            }
            return Is(uri, "dota2", "running") ? Json(Match(1, 1699)) : "[]";
        });
        using var provider = Build(handler);
        var day = await Load(provider);
        Assert.Equal(102, day.Matches.Count);
        Assert.Equal(2, day.Matches.Count(match => match.Id == 1));
        Assert.Contains(handler.Requests, uri => Is(uri, "csgo", "running") && uri.Query.Split('&').Contains("page=2", StringComparer.Ordinal));
    }

    [Fact]
    public async Task DoesNotReturnPartialScheduleWhenPaginationNeverEnds()
    {
        var page = Json(Enumerable.Range(1, 100).Select(id => Match(id, 3216)).ToArray());
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "upcoming") ? page : "[]"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(provider));
    }

    [Fact]
    public async Task DeduplicatesStatusSnapshotsAndMatchesWithTwoFollowedOpponents()
    {
        using var provider = Build(new StubHandler(uri => uri.AbsolutePath.StartsWith("/csgo/", StringComparison.Ordinal)
            ? Json(Match(1, 3216, opponent: 131441)) : "[]"));
        Assert.Single((await Load(provider)).Matches);
    }

    [Fact]
    public async Task UsesHalfOpenKyivDayAndKeepsOvernightRunningAndCompletedMatches()
    {
        var handler = new StubHandler(uri =>
        {
            if (Is(uri, "csgo", "upcoming"))
                return Json(Match(1, 3216, begin: "2026-09-04T21:00:00Z"),
                    Match(2, 3216, begin: "2026-09-05T21:00:00Z"));
            if (Is(uri, "csgo", "running"))
                return Json(Match(3, 3216, begin: "2026-09-04T18:00:00Z"));
            if (Is(uri, "csgo", "past"))
                return Json(Match(4, 3216, begin: "2026-09-04T18:00:00Z", end: "2026-09-04T22:00:00Z"));
            return "[]";
        });
        using var provider = Build(handler, new PandaScoreOptions { Token = "test", TimeZoneId = "Europe/Kyiv" });
        var day = await Load(provider);
        Assert.Equal(new DateOnly(2026, 9, 5), day.Date);
        Assert.Equal(new long[] { 1, 3, 4 }, day.Matches.Select(match => match.Id).Order().ToArray());
        Assert.All(handler.Requests.Where(uri => uri.AbsolutePath.EndsWith("/running", StringComparison.Ordinal)),
            uri => Assert.DoesNotContain("range", uri.Query, StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => Uri.UnescapeDataString(uri.Query).Contains("range[end_at]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppliesCs2TitleFilterOnlyToCsAndSendsBearerHeader()
    {
        var handler = new StubHandler(_ => "[]");
        using var provider = Build(handler);
        await Load(provider);
        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, uri =>
            Assert.Equal(uri.AbsolutePath.StartsWith("/csgo/", StringComparison.Ordinal),
                Uri.UnescapeDataString(uri.Query).Contains("filter[videogame_title]=cs-2", StringComparison.Ordinal)));
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer test", value));
    }

    [Fact]
    public async Task UnconfiguredDisciplineMakesNoRequests()
    {
        var handler = new StubHandler(_ => "[]");
        using var provider = Build(handler, new PandaScoreOptions { Token = "test", Dota2TeamIds = [] });
        await Load(provider);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ProviderFailureIsNotAnEmptySuccessfulSchedule()
    {
        using var provider = Build(new StubHandler(_ => "[]", HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<HttpRequestException>(() => Load(provider));
    }

    [Fact]
    public async Task MissingTokenFailsBeforeNetworkRequest()
    {
        var handler = new StubHandler(_ => "[]");
        using var provider = Build(handler, new PandaScoreOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(provider));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NullResponseIsNotTreatedAsAnEmptySuccessfulFeed()
    {
        using var provider = Build(new StubHandler(_ => "null"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(provider));
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var provider = Build(new StubHandler(_ => "[]"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IPandaScoreClient>().GetMatchesForDayAsync(Now, cancellation.Token));
    }

    [Theory]
    [InlineData("2026-03-29T12:00:00Z", 23)]
    [InlineData("2026-10-25T12:00:00Z", 25)]
    public async Task KyivDayRangeAccountsForDaylightSavingChanges(string referenceTime, int expectedHours)
    {
        var handler = new StubHandler(_ => "[]");
        using var provider = Build(handler, new PandaScoreOptions { Token = "test", TimeZoneId = "Europe/Kyiv" });
        await provider.GetRequiredService<IPandaScoreClient>().GetMatchesForDayAsync(
            DateTimeOffset.Parse(referenceTime, System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        var uri = handler.Requests.First(request => Is(request, "csgo", "upcoming"));
        var bounds = Uri.UnescapeDataString(uri.Query).Split("range[begin_at]=", StringSplitOptions.None)[1].Split('&')[0].Split(',');
        var start = DateTimeOffset.Parse(bounds[0], System.Globalization.CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expectedHours, (end - start).TotalHours);
    }

    [Fact]
    public async Task UnknownOpponentSlotsDoNotCrashOrAccidentallyMatch()
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "running")
            ? "[{\"id\":1,\"name\":\"TBD\",\"status\":\"running\",\"opponents\":[{\"opponent\":null}]}]" : "[]"));
        Assert.Empty((await Load(provider)).Matches);
    }

    [Fact]
    public async Task JsonConfigurationReplacesDefaultIdsAndCanDisableADiscipline()
    {
        const string configurationJson = """
            {"PandaScore":{"Token":"test","TimeZoneId":"UTC","CounterStrikeTeamIds":[123],"Dota2TeamIds":[]}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(configurationJson));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        var handler = new StubHandler(uri => Is(uri, "csgo", "running")
            ? Json(Match(1, 3216), Match(2, 123)) : "[]");
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(2, Assert.Single((await Load(provider)).Matches).Id);
        Assert.Equal(3, handler.Requests.Count);
    }

    private static bool Is(Uri uri, string game, string status) =>
        uri.AbsolutePath == "/" + game + "/matches/" + status;

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task AliasIdsAreDefaultsUnlessAnExplicitTeamListOverridesThem(bool useOverride, long expectedMatchId)
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "running")
            ? Json(Match(1, 3216), Match(2, 3210)) : "[]"), new PandaScoreOptions
        {
            Token = "test",
            TimeZoneId = "UTC",
            CounterStrikeTeamAliases = new() { ["G2"] = 3210 },
            CounterStrikeTeamIds = useOverride ? [3216] : null,
            Dota2TeamIds = []
        });

        Assert.Equal(expectedMatchId, Assert.Single((await Load(provider)).Matches).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuildFeedUsesSelectedTeamsAndIncludesRemindersAfterMidnight(bool useSavedCatalog)
    {
        using var provider = Build(new StubHandler(uri => Is(uri, "csgo", "upcoming")
            ? Json(Match(1, 3216, begin: "2026-09-06T00:15:00Z"), Match(2, 3210, begin: "2026-09-06T00:15:00Z"))
            : "[]"));
        var feed = provider.GetRequiredService<IGuildMatchFeed>();
        var day = await feed.GetMatchesAsync(new DateTimeOffset(2026, 9, 5, 23, 45, 0, TimeSpan.Zero),
            "UTC", new GuildEsportsPreferences(useSavedCatalog ? null : [3210], [], ReminderMinutes: 45,
                CounterStrikeTeamAliases: useSavedCatalog ? new() { ["G2"] = 3210 } : null), CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 9, 5), day.Date);
        Assert.Equal(2, Assert.Single(day.Matches).Id);
    }

    private static object Match(long id, long team, long opponent = 999, string? tier = "s",
        string name = "Main team", string begin = "2026-09-05T12:00:00Z", string end = "2026-09-05T15:00:00Z") => new
        {
            id, name = "Team A vs Team B", begin_at = begin, end_at = end, status = "running",
            tournament = new { id = 7, name = "Finals", tier },
            opponents = new[] { new { opponent = new { id = team, name } },
                new { opponent = new { id = opponent, name = "Opponent" } } },
            games = Array.Empty<object>()
        };

    private static string Json(params object[] matches) => JsonSerializer.Serialize(matches);

    private static ServiceProvider Build(StubHandler handler, PandaScoreOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(options ?? new PandaScoreOptions { Token = "test", TimeZoneId = "UTC" }));
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        return services.BuildServiceProvider();
    }

    private static Task<EsportsMatchDay> Load(ServiceProvider provider) =>
        provider.GetRequiredService<IPandaScoreClient>().GetMatchesForDayAsync(Now, CancellationToken.None);

    private sealed class StubHandler(Func<Uri, string> response, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string?> Authorization { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(code)
                { Content = new StringContent(response(request.RequestUri!), Encoding.UTF8, "application/json") });
        }
    }
}
