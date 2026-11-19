using System.Globalization;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Watch;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Watch;

/// <summary>A watchlist entry with its catalog details, when known.</summary>
public sealed record WatchEntry(WatchItem Item, MediaInfo? Media)
{
    /// <summary>Episodes in the current season, when the catalog knows.</summary>
    public int? SeasonEpisodes => Media?.Seasons.FirstOrDefault(season => season.Number == Item.Season)?.Episodes is > 0 and var count ? count : null;

    /// <summary>Episodes before the next one, across all seasons, for a progress bar. Null without catalog seasons.</summary>
    public int? EpisodesSeen => Media is { Seasons.Count: > 0 } media
        ? Item.Status == WatchStatus.Watched ? media.EpisodeCount
            : Math.Min(media.EpisodeCount ?? 0, media.Seasons.Where(season => season.Number < Item.Season).Sum(season => season.Episodes) + Item.Episode - 1)
        : null;

    /// <summary>The seasons the catalog knows, with their episode counts, for the season picker.</summary>
    public IReadOnlyList<MediaSeason> Seasons => Media?.Seasons.Where(season => season.Episodes > 0).OrderBy(season => season.Number).ToArray() ?? [];
}

/// <param name="CanEdit">The organizer and moderators change the watchlist; members follow it.</param>
public sealed record WatchBoard(Guid GroupId, bool CanEdit, IReadOnlyList<WatchEntry> Entries, bool SupportsFilms);

public sealed record GetWatchlistQuery(GroupActor Actor, Guid GroupId) : IQuery<WatchBoard>;

/// <summary>
/// Adds a title by catalog reference (from a search), by link, or by name only. <paramref name="Kind"/> is series, film
/// or anime.
/// </summary>
public sealed record AddWatchItemCommand(GroupActor Actor, Guid GroupId, string Kind, MediaRef? Reference, string? Url, string? Title)
    : ICommand<WatchItem>;

/// <summary>
/// Changes an entry. Null values stay as they are. <paramref name="NextEpisode"/> marks the current episode (or the film)
/// as watched and moves on; <paramref name="Finish"/> marks the whole series (or film) watched; <paramref name="Position"/>
/// is where the group stopped, like "12:30" or "1:05:00", and an empty string clears it. When the catalog knows the
/// seasons, the season and episode must exist in it.
/// </summary>
public sealed record UpdateWatchItemCommand(GroupActor Actor, Guid GroupId, Guid ItemId, WatchStatus? Status = null, int? Season = null,
    int? Episode = null, string? Position = null, bool NextEpisode = false, bool Finish = false) : ICommand<WatchItem>;

public sealed record RemoveWatchItemCommand(GroupActor Actor, Guid GroupId, Guid ItemId) : ICommand<bool>;

public static class WatchKinds
{
    public static readonly string[] All = ["series", "anime", "film"];
    public static bool HasEpisodes(string kind) => kind is "series" or "anime";

    /// <summary>"12:30" or "1:05:00" as seconds; null when it isn't a time.</summary>
    public static int? ParsePosition(string? text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length is < 1 or > 3 || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return null;
        var numbers = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Skip(1).Any(number => number > 59))
            return null;
        int seconds = numbers.Aggregate(0, (total, number) => total * 60 + number);
        return parts.Length == 1 ? seconds * 60 : seconds;
    }

    public static string FormatPosition(int seconds) => seconds >= 3600
        ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
}

internal sealed class WatchHandlers(IWatchStore store, IMediaCatalog catalog, IClock clock, ISender sender, IAccountLinkStore accountLinks)
    : IQueryHandler<GetWatchlistQuery, WatchBoard>, ICommandHandler<AddWatchItemCommand, WatchItem>,
      ICommandHandler<UpdateWatchItemCommand, WatchItem>, ICommandHandler<RemoveWatchItemCommand, bool>
{
    private static readonly ErrorDescriptor NotFound = new("Watch.NotFound", "That group or title doesn't exist.");
    private static readonly ErrorDescriptor Forbidden = new("Watch.Forbidden", "Only the organizer or a moderator can change the watchlist.");

    public async Task<Result<WatchBoard>> Handle(GetWatchlistQuery query, CancellationToken cancellationToken)
    {
        if (await GroupAsync(query.Actor, query.GroupId) is not { CanViewRoster: true } group)
            return Result.Failure<WatchBoard>(NotFound);
        var entries = new List<WatchEntry>();
        foreach (var item in await store.ListAsync(query.Actor.GuildId, query.GroupId))
            entries.Add(new WatchEntry(item, item.Source is { } source && item.ExternalId is { } id
                ? await catalog.GetAsync(new MediaRef(source, id), cancellationToken) : null));
        var ordered = entries.OrderBy(entry => entry.Item.Status switch { WatchStatus.Watching => 0, WatchStatus.Planned => 1, WatchStatus.Watched => 2, _ => 3 })
            .ThenByDescending(entry => entry.Item.Status == WatchStatus.Watched ? entry.Item.FinishedAt : entry.Item.UpdatedAt).ToArray();
        return new WatchBoard(query.GroupId, group.Summary.CanManage, ordered, catalog.SupportsFilms);
    }

    public async Task<Result<WatchItem>> Handle(AddWatchItemCommand command, CancellationToken cancellationToken)
    {
        if (await GroupAsync(command.Actor, command.GroupId) is not { } group)
            return Result.Failure<WatchItem>(NotFound);
        if (!group.Summary.CanManage)
            return Result.Failure<WatchItem>(Forbidden);
        if (!WatchKinds.All.Contains(command.Kind))
            return Result.Failure<WatchItem>(new ErrorDescriptor("Watch.Kind", "Choose a series, film or anime."));

        var media = command.Reference is { } reference ? await catalog.GetAsync(reference, cancellationToken)
            : command.Url is { Length: > 0 } url ? await catalog.LookupAsync(url, cancellationToken) : null;
        string title = (media?.Title ?? command.Title ?? "").Trim();
        if (title.Length is 0 or > 120)
            return Result.Failure<WatchItem>(new ErrorDescriptor("Watch.Title",
                command.Url is { Length: > 0 } ? "That link wasn't recognised. Add the title too." : "Search for a title, paste a link, or type a title."));
        string? link = media?.Url ?? (Uri.TryCreate(command.Url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" ? parsed.ToString() : null);
        var now = clock.UtcNow;
        ulong addedBy = (await accountLinks.LoadAsync(command.Actor.GuildId)).Member(command.Actor.UserId);
        return await store.AddAsync(new WatchItem(Guid.NewGuid(), command.Actor.GuildId, command.GroupId, media?.Kind is "series" or "film" or "anime" ? media.Kind : command.Kind,
            title, link, media?.ImageUrl, media?.Source, media?.ExternalId, WatchStatus.Planned, 1, 1, null, addedBy, now, now, null));
    }

    public async Task<Result<WatchItem>> Handle(UpdateWatchItemCommand command, CancellationToken cancellationToken)
    {
        var (item, error) = await EditableAsync(command.Actor, command.GroupId, command.ItemId);
        if (item is null)
            return Result.Failure<WatchItem>(error!);
        var now = clock.UtcNow;
        var updated = item with { UpdatedAt = now };
        var seasons = command.Season is not null || command.Episode is not null || command.Finish || command.Status == WatchStatus.Watched
            ? await SeasonsAsync(item, cancellationToken) : [];
        if (command.Season is { } season && season != item.Season)
        {
            if (season < 1 || seasons.Length > 0 && seasons.All(entry => entry.Number != season) || season > 200)
                return Result.Failure<WatchItem>(new ErrorDescriptor("Watch.Season", "Pick a season this series has."));
            // A new season starts at its first episode unless an episode is given too.
            updated = updated with { Season = season, Episode = 1, PositionSeconds = null };
        }
        if (command.Episode is { } episode && (episode != item.Episode || updated.Season != item.Season))
        {
            int? limit = seasons.FirstOrDefault(entry => entry.Number == updated.Season)?.Episodes;
            if (episode < 1 || episode > (limit ?? 5000))
                return Result.Failure<WatchItem>(new ErrorDescriptor("Watch.Episode", "Pick an episode this season has."));
            updated = updated with { Episode = episode, PositionSeconds = null };
        }
        if (command.Position is { } position)
        {
            if (position.Trim().Length == 0)
                updated = updated with { PositionSeconds = null };
            else if (WatchKinds.ParsePosition(position) is { } seconds && seconds <= 24 * 3600)
                updated = updated with { PositionSeconds = seconds, Status = updated.Status == WatchStatus.Planned ? WatchStatus.Watching : updated.Status };
            else
                return Result.Failure<WatchItem>(new ErrorDescriptor("Watch.Position", "Write where you stopped like 12:30 or 1:05:00."));
        }
        if (command.NextEpisode)
            updated = await AdvanceAsync(updated, now, cancellationToken);
        if (command.Finish || command.Status == WatchStatus.Watched && updated.Status != WatchStatus.Watched)
            updated = Finished(updated, seasons, now);
        else if (command.Status is { } status && status != updated.Status)
            updated = updated with { Status = status, FinishedAt = null };
        await store.SaveAsync(updated);
        return updated;
    }

    public async Task<Result<bool>> Handle(RemoveWatchItemCommand command, CancellationToken cancellationToken)
    {
        var (item, error) = await EditableAsync(command.Actor, command.GroupId, command.ItemId);
        return item is null ? Result.Failure<bool>(error!) : await store.RemoveAsync(command.Actor.GuildId, item.Id);
    }

    /// <summary>
    /// Marks the current episode (or the film) watched. The next episode follows, rolling into the next season when the
    /// catalog knows the season's length; after the last episode the title is watched.
    /// </summary>
    private async Task<WatchItem> AdvanceAsync(WatchItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!WatchKinds.HasEpisodes(item.Kind))
            return item with { Status = WatchStatus.Watched, FinishedAt = now, PositionSeconds = null };
        var media = item.Source is { } source && item.ExternalId is { } id ? await catalog.GetAsync(new MediaRef(source, id), cancellationToken) : null;
        var seasons = media?.Seasons.Where(season => season.Episodes > 0).OrderBy(season => season.Number).ToArray() ?? [];
        int season = item.Season, episode = item.Episode + 1;
        if (seasons.FirstOrDefault(entry => entry.Number == season) is { } current && episode > current.Episodes)
        {
            if (seasons.FirstOrDefault(entry => entry.Number > season) is { } next)
                (season, episode) = (next.Number, 1);
            else
                return item with { Status = WatchStatus.Watched, FinishedAt = now, PositionSeconds = null };
        }
        return item with { Season = season, Episode = episode, PositionSeconds = null, Status = WatchStatus.Watching };
    }

    /// <summary>The whole series (or film) watched: the last episode, no resume point, and when it was finished.</summary>
    private static WatchItem Finished(WatchItem item, MediaSeason[] seasons, DateTimeOffset now) => seasons.Length > 0 && WatchKinds.HasEpisodes(item.Kind)
        ? item with { Status = WatchStatus.Watched, FinishedAt = now, PositionSeconds = null, Season = seasons[^1].Number, Episode = seasons[^1].Episodes }
        : item with { Status = WatchStatus.Watched, FinishedAt = now, PositionSeconds = null };

    /// <summary>The seasons the catalog knows for the item, with episodes, in order; empty when it doesn't know.</summary>
    private async Task<MediaSeason[]> SeasonsAsync(WatchItem item, CancellationToken cancellationToken) =>
        item.Source is { } source && item.ExternalId is { } id && await catalog.GetAsync(new MediaRef(source, id), cancellationToken) is { } media
            ? media.Seasons.Where(season => season.Episodes > 0).OrderBy(season => season.Number).ToArray()
            : [];

    private async Task<(WatchItem? Item, ErrorDescriptor? Error)> EditableAsync(GroupActor actor, Guid groupId, Guid itemId)
    {
        if (await GroupAsync(actor, groupId) is not { } group || await store.GetAsync(actor.GuildId, itemId) is not { } item || item.GroupId != groupId)
            return (null, NotFound);
        return group.Summary.CanManage ? (item, null) : (null, Forbidden);
    }

    private async Task<GroupDetail?> GroupAsync(GroupActor actor, Guid groupId) =>
        (await sender.Send(new GetGroupDetailQuery(actor, groupId))) is { IsSuccess: true } result ? result.Value : null;
}
