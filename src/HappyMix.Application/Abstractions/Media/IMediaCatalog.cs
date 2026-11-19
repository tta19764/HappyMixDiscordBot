namespace HappyMix.Application.Abstractions.Media;

/// <summary>
/// What is known about a game, series, film or anime from a public catalog. <see cref="Source"/> names the catalog
/// (steam, tvmaze, anilist, tmdb) and <see cref="ExternalId"/> the item in it; together they identify the item.
/// </summary>
public sealed record MediaInfo(
    string Kind,
    string Source,
    string ExternalId,
    string Title,
    string? Url,
    string? ImageUrl,
    int? Year,
    string? Summary,
    IReadOnlyList<string> Genres,
    double? Rating,
    string? Status,
    int? Runtime,
    IReadOnlyList<MediaSeason> Seasons)
{
    public int? EpisodeCount => Seasons.Count == 0 ? null : Seasons.Sum(season => season.Episodes);

    /// <summary>The reference stored with poll options and watchlist items.</summary>
    public MediaRef Ref => new(Source, ExternalId);
}

/// <summary>A season and how many episodes it has. Anime and miniseries have a single season.</summary>
public sealed record MediaSeason(int Number, int Episodes);

public sealed record MediaRef(string Source, string ExternalId);

/// <summary>
/// Looks titles up in free public catalogs: Steam for games, TVmaze for series, AniList for anime (MyAnimeList links are matched by their id) and,
/// when an API key is configured, TMDB for films. Results are cached. Every call is best effort: a catalog that is
/// unreachable or doesn't know the item returns nothing rather than failing.
/// </summary>
public interface IMediaCatalog
{
    /// <summary>Up to ten matches for <paramref name="query"/> among items of <paramref name="kind"/> (game, series, film, anime).</summary>
    Task<IReadOnlyList<MediaInfo>> SearchAsync(string kind, string query, CancellationToken cancellationToken);

    /// <summary>The item a store, IMDb, TVmaze, MyAnimeList or TMDB link points at, when the link is recognised.</summary>
    Task<MediaInfo?> LookupAsync(string url, CancellationToken cancellationToken);

    /// <summary>A known item by reference, from the cache when it is fresh.</summary>
    Task<MediaInfo?> GetAsync(MediaRef reference, CancellationToken cancellationToken);

    /// <summary>Whether film lookups are available (they need a TMDB key).</summary>
    bool SupportsFilms { get; }
}
