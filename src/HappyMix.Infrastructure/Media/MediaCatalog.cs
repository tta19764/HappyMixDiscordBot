using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HappyMix.Infrastructure.Media;

/// <summary>
/// Free public catalogs: the Steam store for games, TVmaze for series, AniList for anime and, when
/// <c>Media:TmdbApiKey</c> is set, TMDB for films. Details are cached for a week in the MediaCache table.
/// </summary>
internal sealed partial class MediaCatalog(IHttpClientFactory clients, IDbContextFactory<HappyMixDbContext> factory, IConfiguration configuration,
    IClock clock, ILogger<MediaCatalog> logger) : IMediaCatalog, IDisposable
{
    public const string HttpClientName = "Media";
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? TmdbKey => configuration["Media:TmdbApiKey"] is { Length: > 0 } key ? key : null;
    private bool HasIgdb => configuration["Twitch:ClientId"] is { Length: > 0 } && configuration["Twitch:ClientSecret"] is { Length: > 0 };
    private string? _igdbToken;
    private DateTimeOffset _igdbExpires;
    private readonly SemaphoreSlim _igdbLock = new(1);

    public void Dispose() => _igdbLock.Dispose();

    public bool SupportsFilms => TmdbKey is not null;

    public async Task<IReadOnlyList<MediaInfo>> SearchAsync(string kind, string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 100)
            return [];
        try
        {
            return kind switch
            {
                "game" when HasIgdb => await SearchIgdbAsync(query, cancellationToken),
                "game" => await SearchSteamAsync(query, cancellationToken),
                "series" => await SearchTvMazeAsync(query, cancellationToken),
                "anime" => await SearchAniListAsync(query, cancellationToken),
                "film" when TmdbKey is not null => await SearchTmdbAsync(query, cancellationToken),
                _ => []
            };
        }
        catch (Exception error) when (IsLookupFailure(error, cancellationToken))
        {
            LogLookupFailed(logger, kind, error.GetType().Name);
            return [];
        }
    }

    public async Task<MediaInfo?> LookupAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        string host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (host)
            {
                case "igdb.com" when segments is ["games", var igdbSlug, ..] && HasIgdb && SlugPattern().IsMatch(igdbSlug):
                    return await FetchIgdbAsync($"where slug = \"{igdbSlug}\";", cancellationToken);
                case "store.epicgames.com" or "epicgames.com" or "gog.com" when HasIgdb && segments.LastOrDefault() is { } storeSlug && SlugPattern().IsMatch(storeSlug):
                    // Store pages name the game in the address: find it on IGDB by that name.
                    return (await SearchIgdbAsync(storeSlug.Replace('-', ' ').Replace('_', ' '), cancellationToken)) is { Count: > 0 } found ? found[0] : null;
                case "store.steampowered.com" when segments is ["app", var app, ..] && uint.TryParse(app, out _):
                    return await GetAsync(new MediaRef("steam", app), cancellationToken);
                case "tvmaze.com" when segments is ["shows", var show, ..] && int.TryParse(show, out _):
                    return await GetAsync(new MediaRef("tvmaze", show), cancellationToken);
                case "myanimelist.net" when segments is ["anime", var mal, ..] && int.TryParse(mal, out _):
                    return await AniListByMalAsync(mal, cancellationToken);
                case "anilist.co" when segments is ["anime", var anime, ..] && int.TryParse(anime, out _):
                    return await GetAsync(new MediaRef("anilist", anime), cancellationToken);
                case "themoviedb.org" when segments is ["movie", var movie, ..] && TmdbId(movie) is { } tmdb:
                    return await GetAsync(new MediaRef("tmdb", tmdb), cancellationToken);
                case "imdb.com" or "m.imdb.com" when segments is ["title", var imdb, ..] && ImdbPattern().IsMatch(imdb):
                    return await LookupImdbAsync(imdb, cancellationToken);
                default:
                    return null;
            }
        }
        catch (Exception error) when (IsLookupFailure(error, cancellationToken))
        {
            LogLookupFailed(logger, host, error.GetType().Name);
            return null;
        }
    }

    public async Task<MediaInfo?> GetAsync(MediaRef reference, CancellationToken cancellationToken)
    {
        if (await ReadCacheAsync(reference, cancellationToken) is { } cached)
            return cached;
        try
        {
            var info = reference.Source switch
            {
                "steam" => await FetchSteamAsync(reference.ExternalId, cancellationToken),
                "igdb" when HasIgdb => await FetchIgdbAsync($"where id = {long.Parse(reference.ExternalId, CultureInfo.InvariantCulture)};", cancellationToken),
                "tvmaze" => await FetchTvMazeAsync(reference.ExternalId, cancellationToken),
                "anilist" => await FetchAniListAsync($"id: {int.Parse(reference.ExternalId, CultureInfo.InvariantCulture)}", cancellationToken),
                "tmdb" when TmdbKey is not null => await FetchTmdbAsync(reference.ExternalId, cancellationToken),
                _ => null
            };
            if (info is not null)
                await WriteCacheAsync(info, cancellationToken);
            return info;
        }
        catch (Exception error) when (IsLookupFailure(error, cancellationToken))
        {
            LogLookupFailed(logger, reference.Source, error.GetType().Name);
            return null;
        }
    }

    // ---------- Steam ----------

    private async Task<IReadOnlyList<MediaInfo>> SearchSteamAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc=US", cancellationToken);
        return Each(Items(document.RootElement, "items"), item => Text(item, "id") is { } id && Text(item, "name") is { } name
            ? new MediaInfo("game", "steam", id, name, $"https://store.steampowered.com/app/{id}", Text(item, "tiny_image"), null, null, [], null, null, null, [])
            : null);
    }

    private async Task<MediaInfo?> FetchSteamAsync(string app, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(app)}&l=english", cancellationToken);
        if (!document.RootElement.TryGetProperty(app, out var wrapper) || !wrapper.TryGetProperty("data", out var data))
            return null;
        string? release = data.TryGetProperty("release_date", out var date) ? Text(date, "date") : null;
        double? score = data.TryGetProperty("metacritic", out var metacritic) && Number(metacritic, "score") is { } metascore ? metascore / 10.0 : null;
        return new MediaInfo("game", "steam", app, Text(data, "name") ?? app, $"https://store.steampowered.com/app/{app}", Text(data, "header_image"),
            YearIn(release), Clean(Text(data, "short_description")), Items(data, "genres").Select(item => Text(item, "description")).OfType<string>().Take(4).ToArray(),
            score, null, null, []);
    }

    // ---------- IGDB (games on every platform) ----------

    private const string IgdbFields = "fields name,slug,url,first_release_date,summary,total_rating,cover.image_id,genres.name,platforms.abbreviation,platforms.name,websites.category,websites.url;";

    private async Task<IReadOnlyList<MediaInfo>> SearchIgdbAsync(string query, CancellationToken cancellationToken)
    {
        string safe = query.Replace("\\", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal);
        using var document = await IgdbAsync($"search \"{safe}\"; {IgdbFields} where version_parent = null; limit 10;", cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Array ? Each(document.RootElement.EnumerateArray(), Igdb) : [];
    }

    /// <param name="filter">A trusted IGDB "where" clause built from a parsed number or a checked slug.</param>
    private async Task<MediaInfo?> FetchIgdbAsync(string filter, CancellationToken cancellationToken)
    {
        using var document = await IgdbAsync($"{IgdbFields} {filter} limit 1;", cancellationToken);
        var info = document.RootElement.ValueKind == JsonValueKind.Array ? Each(document.RootElement.EnumerateArray(), Igdb, 1).FirstOrDefault() : null;
        if (info is not null)
            await WriteCacheAsync(info, cancellationToken);
        return info;
    }

    private async Task<JsonDocument> IgdbAsync(string body, CancellationToken cancellationToken)
    {
        string token = await IgdbTokenAsync(cancellationToken);
        using var client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games") { Content = new StringContent(body) };
        request.Headers.Add("Client-ID", configuration["Twitch:ClientId"]);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            _igdbToken = null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>An app access token from Twitch, which runs IGDB; the same credentials as the Twitch stream lookups.</summary>
    private async Task<string> IgdbTokenAsync(CancellationToken cancellationToken)
    {
        await _igdbLock.WaitAsync(cancellationToken);
        try
        {
            if (_igdbToken is not null && clock.UtcNow < _igdbExpires)
                return _igdbToken;
            using var client = clients.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = configuration["Twitch:ClientId"]!,
                ["client_secret"] = configuration["Twitch:ClientSecret"]!,
                ["grant_type"] = "client_credentials"
            });
            using var response = await client.PostAsync("https://id.twitch.tv/oauth2/token", content, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            _igdbToken = Text(document.RootElement, "access_token") ?? throw new HttpRequestException("Twitch returned no access token.");
            _igdbExpires = clock.UtcNow.AddSeconds(Math.Max(60, (Int(document.RootElement, "expires_in") ?? 3600) - 120));
            return _igdbToken;
        }
        finally
        {
            _igdbLock.Release();
        }
    }

    internal static MediaInfo? Igdb(JsonElement game)
    {
        if (Text(game, "id") is not { } id || Text(game, "name") is not { } name)
            return null;
        string? cover = game.TryGetProperty("cover", out var image) && Text(image, "image_id") is { } imageId
            ? $"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId}.jpg" : null;
        int? year = Number(game, "first_release_date") is { } released ? DateTimeOffset.FromUnixTimeSeconds((long)released).Year : null;
        var websites = Items(game, "websites");
        // IGDB marks Steam store links with category 13; keeping the app id lets Steam features (news) work for the game.
        string? steam = websites.Where(site => Int(site, "category") == 13).Select(site => Text(site, "url"))
            .Select(url => url is null ? null : Regex.Match(url, @"store\.steampowered\.com/app/(\d+)", RegexOptions.None, TimeSpan.FromMilliseconds(50)))
            .Where(match => match is { Success: true }).Select(match => match!.Groups[1].Value).FirstOrDefault();
        string? official = websites.Where(site => Int(site, "category") == 1).Select(site => Text(site, "url")).FirstOrDefault();
        var platforms = Items(game, "platforms").Select(platform => Text(platform, "abbreviation") ?? Text(platform, "name")).OfType<string>()
            .Distinct().Take(6).ToArray();
        return new MediaInfo("game", "igdb", id, name, official ?? Text(game, "url"), cover, year, Clean(Text(game, "summary")),
            Items(game, "genres").Select(genre => Text(genre, "name")).OfType<string>().Take(3).ToArray(),
            Number(game, "total_rating") is { } rating ? Math.Round(rating / 10, 1) : null, null, null, [], platforms, steam);
    }

    // ---------- TVmaze (series) ----------

    private async Task<IReadOnlyList<MediaInfo>> SearchTvMazeAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(query)}", cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? Each(document.RootElement.EnumerateArray(), item => item.TryGetProperty("show", out var show) ? TvMaze(show, []) : null)
            : [];
    }

    private async Task<MediaInfo?> FetchTvMazeAsync(string id, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://api.tvmaze.com/shows/{Uri.EscapeDataString(id)}?embed=seasons", cancellationToken);
        var seasons = document.RootElement.TryGetProperty("_embedded", out var embedded)
            ? Items(embedded, "seasons").Select(item => (Number: Int(item, "number"), Episodes: Int(item, "episodeOrder")))
                .Where(item => item.Number > 0).Select(item => new MediaSeason(item.Number!.Value, item.Episodes ?? 0)).ToArray()
            : [];
        return TvMaze(document.RootElement, seasons);
    }

    internal static MediaInfo[] ParseTvMazeSearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? Each(document.RootElement.EnumerateArray(), item => item.TryGetProperty("show", out var show) ? TvMaze(show, []) : null)
            : [];
    }

    internal static MediaInfo? ParseAniList(string json)
    {
        using var document = JsonDocument.Parse(json);
        return AniList(document.RootElement);
    }

    private static MediaInfo? TvMaze(JsonElement show, MediaSeason[] seasons)
    {
        string? id = Text(show, "id"), name = Text(show, "name");
        if (id is null || name is null)
            return null;
        string? image = show.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Object ? Text(images, "medium") : null;
        double? rating = show.TryGetProperty("rating", out var score) ? Number(score, "average") : null;
        return new MediaInfo("series", "tvmaze", id, name, Text(show, "url"), image, YearIn(Text(show, "premiered")), Clean(Text(show, "summary")),
            Items(show, "genres").Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).OfType<string>().Take(4).ToArray(), rating, Text(show, "status"),
            Int(show, "averageRuntime") ?? Int(show, "runtime"), seasons);
    }

    // ---------- AniList (anime) ----------

    private const string AniListFields = "id idMal title { english romaji } seasonYear episodes duration averageScore status genres coverImage { large } siteUrl description(asHtml: false)";

    private async Task<IReadOnlyList<MediaInfo>> SearchAniListAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await AniListAsync("query ($q: String) { Page(perPage: 10) { media(search: $q, type: ANIME, isAdult: false) { " + AniListFields + " } } }",
            new { q = query }, cancellationToken);
        return document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("Page", out var page)
            ? Each(Items(page, "media"), AniList) : [];
    }

    private async Task<MediaInfo?> AniListByMalAsync(string mal, CancellationToken cancellationToken)
    {
        var info = await FetchAniListAsync($"idMal: {int.Parse(mal, CultureInfo.InvariantCulture)}", cancellationToken);
        if (info is not null)
            await WriteCacheAsync(info, cancellationToken);
        return info;
    }

    /// <param name="filter">A trusted GraphQL argument such as "id: 5114"; callers pass parsed numbers only.</param>
    private async Task<MediaInfo?> FetchAniListAsync(string filter, CancellationToken cancellationToken)
    {
        using var document = await AniListAsync("query { Media(" + filter + ", type: ANIME) { " + AniListFields + " } }", null, cancellationToken);
        return document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("Media", out var media) ? AniList(media) : null;
    }

    private async Task<JsonDocument> AniListAsync(string query, object? variables, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync("https://graphql.anilist.co", new { query, variables }, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static MediaInfo? AniList(JsonElement anime)
    {
        if (anime.ValueKind != JsonValueKind.Object || Text(anime, "id") is not { } id)
            return null;
        var titles = anime.TryGetProperty("title", out var title) ? title : default;
        string? name = titles.ValueKind == JsonValueKind.Object ? Text(titles, "english") ?? Text(titles, "romaji") : null;
        if (name is null)
            return null;
        string? image = anime.TryGetProperty("coverImage", out var cover) && cover.ValueKind == JsonValueKind.Object ? Text(cover, "large") : null;
        int? score = Int(anime, "averageScore");
        int? episodes = Int(anime, "episodes");
        return new MediaInfo("anime", "anilist", id, name, Text(anime, "siteUrl"), image, Int(anime, "seasonYear"), Clean(Text(anime, "description")),
            Items(anime, "genres").Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).OfType<string>().Take(4).ToArray(), score / 10.0,
            Text(anime, "status") is { } status ? status.Replace('_', ' ').ToLowerInvariant() : null, Int(anime, "duration"),
            episodes is > 0 ? [new MediaSeason(1, episodes.Value)] : []);
    }

    // ---------- TMDB (films, optional) ----------

    private async Task<IReadOnlyList<MediaInfo>> SearchTmdbAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://api.themoviedb.org/3/search/movie?query={Uri.EscapeDataString(query)}&api_key={TmdbKey}", cancellationToken);
        return Each(Items(document.RootElement, "results"), Tmdb);
    }

    private async Task<MediaInfo?> FetchTmdbAsync(string id, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://api.themoviedb.org/3/movie/{Uri.EscapeDataString(id)}?api_key={TmdbKey}", cancellationToken);
        return Tmdb(document.RootElement);
    }

    private static MediaInfo? Tmdb(JsonElement movie)
    {
        string? id = Text(movie, "id"), title = Text(movie, "title");
        if (id is null || title is null)
            return null;
        string? poster = Text(movie, "poster_path");
        double? rating = Number(movie, "vote_average") is > 0 and var average ? average : null;
        return new MediaInfo("film", "tmdb", id, title, $"https://www.themoviedb.org/movie/{id}", poster is null ? null : "https://image.tmdb.org/t/p/w342" + poster,
            YearIn(Text(movie, "release_date")), Clean(Text(movie, "overview")),
            Items(movie, "genres").Select(item => Text(item, "name")).OfType<string>().Take(4).ToArray(), rating, Text(movie, "status"), Int(movie, "runtime"), []);
    }

    /// <summary>An IMDb title is looked up as a series on TVmaze first, then as a film on TMDB.</summary>
    private async Task<MediaInfo?> LookupImdbAsync(string imdb, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.GetAsync($"https://api.tvmaze.com/lookup/shows?imdb={imdb}", cancellationToken);
        if (response.IsSuccessStatusCode && (await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken)) is { ValueKind: JsonValueKind.Object } show
            && Text(show, "id") is { } id)
            return await GetAsync(new MediaRef("tvmaze", id), cancellationToken);
        if (TmdbKey is null)
            return null;
        using var document = await GetJsonAsync($"https://api.themoviedb.org/3/find/{imdb}?external_source=imdb_id&api_key={TmdbKey}", cancellationToken);
        return Items(document.RootElement, "movie_results").Select(item => Text(item, "id")).OfType<string>().FirstOrDefault() is { } movie
            ? await GetAsync(new MediaRef("tmdb", movie), cancellationToken) : null;
    }

    // ---------- Cache ----------

    private async Task<MediaInfo?> ReadCacheAsync(MediaRef reference, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        long fresh = (clock.UtcNow - CacheAge).ToUnixTimeSeconds();
        string? json = await db.Database.SqlQuery<string>(
                $"SELECT Json AS Value FROM MediaCache WHERE Source = {reference.Source} AND ExternalId = {reference.ExternalId} AND FetchedUtc >= {fresh}")
            .SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<MediaInfo>(json, Json);
    }

    private async Task WriteCacheAsync(MediaInfo info, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        string json = JsonSerializer.Serialize(info, Json);
        long now = clock.UtcNow.ToUnixTimeSeconds();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO MediaCache (Source, ExternalId, Json, FetchedUtc) VALUES ({info.Source}, {info.ExternalId}, {json}, {now})
            ON CONFLICT (Source, ExternalId) DO UPDATE SET Json = excluded.Json, FetchedUtc = excluded.FetchedUtc
            """, cancellationToken);
    }

    // ---------- Helpers ----------

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static JsonElement[] Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()] : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    /// <summary>A whole number, or null when the field is missing, null or not a whole number (catalogs send null for unknown values).</summary>
    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number) ? number : null;

    /// <summary>A number, or null when the field is missing, null or not a number.</summary>
    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number) ? number : null;

    /// <summary>
    /// Converts each search result on its own, so one entry with unexpected data is skipped instead of hiding every result.
    /// </summary>
    private static MediaInfo[] Each(IEnumerable<JsonElement> items, Func<JsonElement, MediaInfo?> convert, int limit = 10)
    {
        var results = new List<MediaInfo>();
        foreach (var item in items)
        {
            try
            {
                if (convert(item) is { } info)
                    results.Add(info);
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or KeyNotFoundException)
            {
                // Skip this entry only.
            }
            if (results.Count == limit)
                break;
        }
        return results.ToArray();
    }

    private static int? YearIn(string? date) =>
        date is not null && Regex.Match(date, @"(19|20)\d{2}", RegexOptions.None, TimeSpan.FromMilliseconds(50)) is { Success: true } match
            ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;

    private static int? MinutesIn(string? duration) =>
        duration is not null && Regex.Match(duration, @"(\d+)\s*min", RegexOptions.None, TimeSpan.FromMilliseconds(50)) is { Success: true } match
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Plain text without HTML, at most 300 characters.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string plain = Regex.Replace(Regex.Replace(text, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100)), @"\s+", " ",
            RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
        plain = System.Net.WebUtility.HtmlDecode(plain);
        return plain.Length <= 300 ? plain : plain[..297].TrimEnd() + "…";
    }

    private static string? TmdbId(string segment) => Regex.Match(segment, @"^\d+", RegexOptions.None, TimeSpan.FromMilliseconds(50)) is { Success: true } match
        ? match.Value : null;

    private static bool IsLookupFailure(Exception error, CancellationToken cancellationToken) => !cancellationToken.IsCancellationRequested &&
        error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException;

    [GeneratedRegex(@"^tt\d{5,10}$", RegexOptions.None, 50)]
    private static partial Regex ImdbPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-_]{0,80}$", RegexOptions.None, 50)]
    private static partial Regex SlugPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media catalog lookup failed for {Catalog} ({FailureType})")]
    private static partial void LogLookupFailed(ILogger logger, string catalog, string failureType);
}
