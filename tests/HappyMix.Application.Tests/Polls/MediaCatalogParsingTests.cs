using HappyMix.Infrastructure.Media;

namespace HappyMix.Application.Tests.Polls;

/// <summary>Catalog responses as the services send them, including the nulls they use for unknown values.</summary>
public sealed class MediaCatalogParsingTests
{
    [Fact]
    public void SeriesSearchKeepsResultsWhenSomeShowsHaveNoRating()
    {
        // Shaped like TVmaze's answer for "Hannibal": several shows have "average": null and no runtime.
        const string json = """
            [
              {"score": 0.9, "show": {"id": 194, "name": "Hannibal", "url": "https://www.tvmaze.com/shows/194/hannibal", "premiered": "2013-04-04",
                "status": "Ended", "runtime": 60, "averageRuntime": 60, "genres": ["Drama", "Crime", "Horror"], "rating": {"average": 8.2},
                "image": {"medium": "https://static.tvmaze.com/194.jpg"}, "summary": "<p><b>Hannibal</b> explores the early relationship.</p>"}},
              {"score": 0.6, "show": {"id": 1971, "name": "Why? With Hannibal Buress", "url": null, "premiered": "2015-07-08", "status": "Ended",
                "runtime": null, "averageRuntime": null, "genres": [], "rating": {"average": null}, "image": null, "summary": null}},
              {"score": 0.5, "show": {"id": 28882, "name": "On Hannibal's Trail", "premiered": null, "rating": {"average": null}, "genres": [null]}}
            ]
            """;
        var results = MediaCatalog.ParseTvMazeSearch(json);

        Assert.Equal(["Hannibal", "Why? With Hannibal Buress", "On Hannibal's Trail"], results.Select(item => item.Title));
        var hannibal = results[0];
        Assert.Equal((2013, 8.2, 60, "Hannibal explores the early relationship."), (hannibal.Year, hannibal.Rating, hannibal.Runtime, hannibal.Summary));
        Assert.Equal(["Drama", "Crime", "Horror"], hannibal.Genres);
        Assert.Null(results[1].Rating);
        Assert.Null(results[1].Runtime);
        Assert.Null(results[2].Year);
    }

    [Fact]
    public void AnimeWithUnknownEpisodesAndYearStillParses()
    {
        const string json = """
            {"id": 21, "idMal": 21, "title": {"english": null, "romaji": "One Piece"}, "seasonYear": null, "episodes": null, "duration": 24,
             "averageScore": null, "status": "RELEASING", "genres": ["Action", null], "coverImage": {"large": "https://img.test/op.jpg"},
             "siteUrl": "https://anilist.co/anime/21", "description": "Gold Roger was known as the <br>Pirate King."}
            """;
        var anime = MediaCatalog.ParseAniList(json)!;
        Assert.Equal(("One Piece", null, null, "releasing"), (anime.Title, anime.Year, anime.Rating, anime.Status));
        Assert.Empty(anime.Seasons);
        Assert.Equal(["Action"], anime.Genres);
        Assert.Equal("Gold Roger was known as the Pirate King.", anime.Summary);
    }
}
