using System.Net;
using System.Net.Http.Json;
using HappyMix.Bot.Services;

namespace HappyMix.Application.Tests.Matches;

public sealed class SteamNewsFeedTests
{
    [Fact]
    public async Task ReadsAllFeedsAndPagesBackToSubscriptionStart()
    {
        using var handler = new FeedHandler();
        using var client = new HttpClient(handler);
        var posts = await GameNewsService.ReadNewsAsync(client, 570, 100, default);
        Assert.Equal(101, posts.Length);
        Assert.Equal(150, posts[0].Date);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.DoesNotContain("feeds=", uri.Query));
        Assert.Contains("enddate=201", handler.Requests[1].Query);
        Assert.DoesNotContain(posts, post => post.Date <= 100);
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var items = Requests.Count == 1
                ? Enumerable.Range(201, 100).Select(date => new GameNewsService.NewsItem(
                    date.ToString(System.Globalization.CultureInfo.InvariantCulture), "Community news", "https://example.com/news", date)).ToArray()
                : [new GameNewsService.NewsItem("older", "Announcement", "https://example.com/news", 150),
                   new GameNewsService.NewsItem("cutoff", "Old news", "https://example.com/news", 100)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { appnews = new { newsitems = items } })
            });
        }
    }
}
