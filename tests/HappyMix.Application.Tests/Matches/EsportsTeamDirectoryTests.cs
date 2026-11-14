using System.Net;
using System.Text;
using HappyMix.Application.Esports;
using HappyMix.Domain.Esports;
using HappyMix.Infrastructure;
using HappyMix.Infrastructure.PandaScore;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HappyMix.Application.Tests.Matches;

public sealed class EsportsTeamDirectoryTests
{
    private const string Teams = """
        [
          {"id": 124530, "name": "Vitality.Bee", "acronym": "VIT.B", "location": "FR", "image_url": null},
          {"id": 3455, "name": "Team Vitality", "acronym": "VIT", "location": "FR", "image_url": "https://cdn.example/vit.png"},
          {"id": 9999, "name": "Vitality Academy", "acronym": "", "location": null, "image_url": ""}
        ]
        """;

    [Fact]
    public async Task SearchUsesTheGamePathRanksCloseMatchesFirstAndIsCached()
    {
        var handler = new StubHandler(_ => Teams);
        using var provider = Build(handler);
        var sender = provider.GetRequiredService<ISender>();

        var first = await sender.Send(new SearchEsportsTeamsQuery(Esport.CounterStrike2, " vit "));
        var again = await sender.Send(new SearchEsportsTeamsQuery(Esport.CounterStrike2, "VIT"));

        Assert.Equal([3455, 124530, 9999], first.Value.Select(team => team.Id));
        Assert.Null(first.Value[2].Acronym);
        Assert.Equal("https://cdn.example/vit.png", first.Value[0].ImageUrl);
        Assert.Equal(3, again.Value.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/csgo/teams", request.AbsolutePath);
        Assert.Contains("search[name]=vit", Uri.UnescapeDataString(request.Query), StringComparison.Ordinal);
        Assert.Equal("Bearer test", Assert.Single(handler.Authorization));

        Assert.Empty((await sender.Send(new SearchEsportsTeamsQuery(Esport.Dota2, "v"))).Value);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NamesForIdsAreLoadedOnceAndUnknownIdsAreLeftOut()
    {
        var handler = new StubHandler(_ => """[{"id": 3216, "name": "Natus Vincere", "acronym": "NAVI", "location": "UA"}]""");
        using var provider = Build(handler);
        var sender = provider.GetRequiredService<ISender>();

        var names = await sender.Send(new GetEsportsTeamsQuery([3216, 404]));
        var cached = await sender.Send(new GetEsportsTeamsQuery([404, 3216]));

        Assert.Equal("Natus Vincere", Assert.Single(names.Value).Name);
        Assert.Single(cached.Value);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/teams", request.AbsolutePath);
        Assert.Contains("filter[id]=3216,404", Uri.UnescapeDataString(request.Query), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Team Vitality", 3455L)]
    [InlineData("vit", 3455L)]
    [InlineData("VIT.B", 124530L)]
    [InlineData("Anything #42", 42L)]
    [InlineData("777", 777L)]
    public async Task ResolvingFindsExactNamesAcronymsAndTypedIds(string text, long expected)
    {
        using var provider = Build(new StubHandler(_ => Teams));
        var result = await provider.GetRequiredService<ISender>().Send(new ResolveEsportsTeamQuery(Esport.CounterStrike2, text));
        Assert.Equal(expected, result.Value.Team?.Id);
    }

    [Fact]
    public async Task AmbiguousAndUnknownNamesReturnCandidatesInsteadOfAGuess()
    {
        using var provider = Build(new StubHandler(uri => uri.Query.Contains("nobody", StringComparison.Ordinal) ? "[]" : Teams));
        var sender = provider.GetRequiredService<ISender>();

        var ambiguous = await sender.Send(new ResolveEsportsTeamQuery(Esport.CounterStrike2, "Vitality"));
        var unknown = await sender.Send(new ResolveEsportsTeamQuery(Esport.CounterStrike2, "nobody"));

        Assert.Null(ambiguous.Value.Team);
        Assert.Equal(3, ambiguous.Value.Candidates.Count);
        Assert.Null(unknown.Value.Team);
        Assert.Empty(unknown.Value.Candidates);
    }

    [Fact]
    public async Task AProviderOutageFailsSearchButNeverBlocksIdsOrNames()
    {
        using var provider = Build(new StubHandler(_ => "", HttpStatusCode.InternalServerError));
        var sender = provider.GetRequiredService<ISender>();

        Assert.Equal("Esports.Unavailable", (await sender.Send(new SearchEsportsTeamsQuery(Esport.Dota2, "spirit"))).Error.Code);
        Assert.Equal("Esports.Unavailable", (await sender.Send(new ResolveEsportsTeamQuery(Esport.Dota2, "spirit"))).Error.Code);
        Assert.Equal(1699, (await sender.Send(new ResolveEsportsTeamQuery(Esport.Dota2, "Team Spirit #1699"))).Value.Team?.Id);
        Assert.Empty((await sender.Send(new GetEsportsTeamsQuery([1699]))).Value);

        using var noToken = Build(new StubHandler(_ => Teams), new PandaScoreOptions { Token = "", TimeZoneId = "UTC" });
        Assert.True((await noToken.GetRequiredService<ISender>().Send(new SearchEsportsTeamsQuery(Esport.Dota2, "spirit"))).IsFailure);
    }

    [Fact]
    public void TeamTextRoundTripsTheIdAndName()
    {
        var team = new HappyMix.Application.Abstractions.Esports.EsportsTeam(3455, "Team Vitality", "VIT", "FR", null);
        Assert.Equal("Team Vitality #3455", TeamText.Format(team));
        Assert.True(TeamText.TryGetId(TeamText.Format(team), out long id));
        Assert.Equal(3455, id);
        Assert.Equal("Team Vitality", TeamText.NameOf("Team Vitality #3455"));
        Assert.False(TeamText.TryGetId("Team Vitality", out _));
        Assert.False(TeamText.TryGetId("#0", out _));
    }

    private static ServiceProvider Build(StubHandler handler, PandaScoreOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging().AddApplication().AddInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(options ?? new PandaScoreOptions { Token = "test", TimeZoneId = "UTC" }));
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler(Func<Uri, string> response, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string?> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(code)
                { Content = new StringContent(response(request.RequestUri!), Encoding.UTF8, "application/json") });
        }
    }
}
