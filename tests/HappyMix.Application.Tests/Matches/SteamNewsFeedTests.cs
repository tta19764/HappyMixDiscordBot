using System.Net;
using System.Net.Http.Json;
using HappyMix.Bot.Services;
using HappyMix.Application.Abstractions.News;

namespace HappyMix.Application.Tests.Matches;

public sealed class SteamNewsFeedTests
{
    [Fact]
    public async Task ReadsAllFeedsAndPagesBackToSubscriptionStart()
    {
        using var handler = new FeedHandler();
        using var client = new HttpClient(handler);
        var posts = await GameNewsService.ReadNewsAsync(client, 570, 100, default);
        Assert.Equal(102, posts.Length);
        Assert.Equal(150, posts[0].Date);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.DoesNotContain("feeds=", uri.Query));
        Assert.Contains(posts, post => post.Gid == "press");
        Assert.Contains("enddate=201", handler.Requests[1].Query);
        Assert.DoesNotContain(posts, post => post.Date <= 100);
    }

    [Fact]
    public void SteamPagePostsSplitIntoPatchNotesAndAnnouncementsAndEverythingElseIsPress()
    {
        GameNewsService.NewsItem Post(string feed, int type, params string[] tags) => new("1", "Title", "https://example.com", 1, feed, feed, type, tags);
        Assert.Equal(GameNewsSources.PatchNotes, GameNewsService.Kind(Post(GameNewsService.OfficialFeed, 1, "patchnotes", "workshop")));
        Assert.Equal(GameNewsSources.Announcements, GameNewsService.Kind(Post(GameNewsService.OfficialFeed, 1, "hide_store")));
        Assert.Equal(GameNewsSources.Press, GameNewsService.Kind(Post("PC Gamer", 0)));
        Assert.Equal(GameNewsSources.Press, GameNewsService.Kind(new GameNewsService.NewsItem("2", "Old item", "https://example.com", 1)));
    }

    [Fact]
    public void SteamNewsJsonKeepsFeedAndTags()
    {
        var item = System.Text.Json.JsonSerializer.Deserialize<GameNewsService.NewsItem>(
            """{"gid":"9","title":"7.42","url":"https://x","date":5,"feedname":"steam_community_announcements","feedlabel":"Community Announcements","feed_type":1,"tags":["patchnotes"]}""",
            System.Text.Json.JsonSerializerOptions.Web)!;
        Assert.Equal(GameNewsSources.PatchNotes, GameNewsService.Kind(item));
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
                : [new GameNewsService.NewsItem("older", "Announcement", "https://example.com/news", 150, GameNewsService.OfficialFeed),
                   new GameNewsService.NewsItem("press", "Review roundup", "https://example.com/press", 160, "PC Gamer"),
                   new GameNewsService.NewsItem("cutoff", "Old news", "https://example.com/news", 100)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { appnews = new { newsitems = items } })
            });
        }
    }
}
