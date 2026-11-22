using System.Text.Json;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Messaging;
using HappyMix.Application.Abstractions.Stats;
using HappyMix.Application.Guilds;
using HappyMix.Domain.Abstractions;
using MediatR;

namespace HappyMix.Application.Stats;

/// <summary>The Game features page: the viewer's own stats (same data as group Stats tabs) and the server's Dota 2 meta.</summary>
/// <param name="MetaError">Why the meta couldn't be loaded, when it couldn't.</param>
public sealed record GameFeatures(MyStats Mine, DotaMetaSettings Meta, DotaMeta? Heroes, string? MetaError, DateTimeOffset Now)
{
    /// <summary>Whether the STRATZ meta search is available (a key is set).</summary>
    public bool SearchEnabled { get; init; }
    public MetaFilter Filter { get; init; } = new();
    /// <summary>The search result for <see cref="Filter"/>, already sorted.</summary>
    public HeroMeta? Search { get; init; }
    public string? SearchError { get; init; }
}

/// <param name="Filter">The meta search's filters; the lane meta is loaded only when the search isn't available.</param>
public sealed record GetGameFeaturesQuery(ulong GuildId, ulong UserId, MetaFilter? Filter = null) : IQuery<GameFeatures>;

/// <summary>The server's meta post settings, for the game list and the channel button.</summary>
public sealed record GetDotaMetaSettingsQuery(ulong GuildId) : IQuery<DotaMetaSettings>;

/// <summary>Saves where and how often the meta is posted. Server admins only (checked by the caller).</summary>
/// <param name="Count">Heroes per lane, from <see cref="DotaMetas.Counts"/>.</param>
public sealed record SaveDotaMetaCommand(ulong GuildId, MetaSchedule Schedule, ulong? ChannelId, DayOfWeek Day, int Hour, int Count = DotaMetas.DefaultCount,
    MetaRank Rank = MetaRank.All, MetaPatch Patch = MetaPatch.Current, MetaSort Sort = MetaSort.Score, double MinPickRate = 1) : ICommand<DotaMetaSettings>;

/// <summary>
/// Posts the meta in the server's meta channel now ("Post now"), with data at most an hour old: by role from STRATZ
/// with the saved filters, or by lane from OpenDota when STRATZ isn't set up or doesn't answer.
/// </summary>
public sealed record PostDotaMetaCommand(ulong GuildId) : ICommand<bool>;

/// <summary>Hourly: posts the meta where it's due by the server's schedule, in the server's time zone. Returns how many were posted.</summary>
public sealed record RunDotaMetaScheduleCommand(IReadOnlyCollection<ulong> GuildIds, string DefaultTimeZone) : ICommand<int>;

public static class DotaMetas
{
    public const string CacheKey = "dota-meta-v2";
    /// <summary>Heroes kept per lane in the cache; each server shows and posts its own <see cref="DotaMetaSettings.Count"/>.</summary>
    public const int MaxPerLane = 20;
    public const int DefaultCount = 10;
    /// <summary>The choices offered for heroes per lane.</summary>
    public static readonly int[] Counts = [5, 10, 15, 20];

    /// <summary>The choices offered for the minimum pick rate, in percent.</summary>
    public static readonly double[] MinPickRates = [0, 0.5, 1, 2, 3, 5];

    /// <summary>The heroes of a search picked in at least <paramref name="minPickRate"/>% of matches, in the chosen order.</summary>
    public static IReadOnlyList<RankedHero> Sorted(IEnumerable<RankedHero> heroes, MetaSort sort, double minPickRate = 0)
    {
        var common = heroes.Where(hero => hero.PickRate >= minPickRate);
        return sort switch
        {
            MetaSort.WinRate => common.OrderByDescending(hero => hero.WinRate).ThenByDescending(hero => hero.Games).ToArray(),
            MetaSort.PickRate => common.OrderByDescending(hero => hero.PickRate).ToArray(),
            _ => common.OrderByDescending(hero => hero.Score).ToArray()
        };
    }

    /// <summary>The cache entry for a filter; the order is applied after reading, so every sort shares one entry.</summary>
    public static string SearchKey(MetaFilter filter) => $"stratz:{filter.Patch}:{filter.Position}:{filter.Rank}";
    /// <summary>The page shows the cached meta for this long before fetching again.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromHours(6);
    /// <summary>A post uses data at most this old.</summary>
    public static readonly TimeSpan PostWith = TimeSpan.FromHours(1);

    /// <summary>Whether a post is due on <paramref name="today"/> (local) at <paramref name="hour"/>, given the last post's local date.</summary>
    public static bool IsDue(DotaMetaSettings settings, DateOnly today, int hour, DateOnly? lastPosted) =>
        settings.ChannelId is not null && hour >= settings.Hour && lastPosted != today && settings.Schedule switch
        {
            MetaSchedule.Daily => true,
            MetaSchedule.EveryTwoDays => lastPosted is null || today.DayNumber - lastPosted.Value.DayNumber >= 2,
            MetaSchedule.Weekly => today.DayOfWeek == settings.Day,
            _ => false
        };
}

internal sealed class GameFeatureHandlers(IGameFeatureStore store, IDotaMetaSource source, IEnumerable<IHeroMetaSource> heroSources,
    IEnumerable<IDotaMetaPublisher> publishers, IGuildSettingsStore settings, IClock clock, ISender sender) :
    IQueryHandler<GetGameFeaturesQuery, GameFeatures>, ICommandHandler<SaveDotaMetaCommand, DotaMetaSettings>, IQueryHandler<GetDotaMetaSettingsQuery, DotaMetaSettings>,
    ICommandHandler<PostDotaMetaCommand, bool>, ICommandHandler<RunDotaMetaScheduleCommand, int>
{
    private IHeroMetaSource? Search => heroSources.FirstOrDefault(item => item.Enabled);

    public async Task<Result<DotaMetaSettings>> Handle(GetDotaMetaSettingsQuery query, CancellationToken cancellationToken) =>
        await store.GetDotaMetaAsync(query.GuildId);

    public async Task<Result<GameFeatures>> Handle(GetGameFeaturesQuery query, CancellationToken cancellationToken)
    {
        var mine = (await sender.Send(new GetMyStatsQuery(query.UserId), cancellationToken)).Value;
        var saved = await store.GetDotaMetaAsync(query.GuildId);
        var filter = query.Filter ?? new MetaFilter(saved.Patch, 0, saved.Rank, saved.Sort, saved.MinPickRate);
        if (Search is not null)
        {
            var (found, problem) = await HeroMetaAsync(filter, DotaMetas.PostWith, cancellationToken);
            // Without a search result the lane meta is still worth showing.
            var (lanes, laneError) = found is null ? await MetaAsync(DotaMetas.ShowFor, cancellationToken) : (null, null);
            return new GameFeatures(mine, saved, lanes, laneError, clock.UtcNow)
            {
                SearchEnabled = true, Filter = filter, SearchError = problem,
                Search = found is null ? null : found with { Filter = filter, Heroes = DotaMetas.Sorted(found.Heroes, filter.Sort, filter.MinPickRate) }
            };
        }
        var (meta, error) = await MetaAsync(DotaMetas.ShowFor, cancellationToken);
        return new GameFeatures(mine, saved, meta, error, clock.UtcNow) { Filter = filter };
    }

    public async Task<Result<DotaMetaSettings>> Handle(SaveDotaMetaCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(command.Schedule) || !Enum.IsDefined(command.Day) || command.Hour is < 0 or > 23 || !DotaMetas.Counts.Contains(command.Count)
            || !Enum.IsDefined(command.Rank) || !Enum.IsDefined(command.Patch) || !Enum.IsDefined(command.Sort) || !DotaMetas.MinPickRates.Contains(command.MinPickRate))
            return Result.Failure<DotaMetaSettings>(new ErrorDescriptor("Features.Invalid", "Choose a valid schedule, day and hour."));
        if (command.Schedule != MetaSchedule.Off && command.ChannelId is null)
            return Result.Failure<DotaMetaSettings>(new ErrorDescriptor("Features.NoChannel", "Choose or create a channel first."));
        var saved = await store.GetDotaMetaAsync(command.GuildId);
        var updated = saved with
        {
            Schedule = command.Schedule, ChannelId = command.ChannelId, Day = command.Day, Hour = command.Hour, Count = command.Count,
            Rank = command.Rank, Patch = command.Patch, Sort = command.Sort, MinPickRate = command.MinPickRate
        };
        await store.SaveDotaMetaAsync(updated);
        return updated;
    }

    public async Task<Result<bool>> Handle(PostDotaMetaCommand command, CancellationToken cancellationToken)
    {
        var saved = await store.GetDotaMetaAsync(command.GuildId);
        if (saved.ChannelId is not { } channel)
            return Result.Failure<bool>(new ErrorDescriptor("Features.NoChannel", "Choose or create a channel first."));

        // By role from STRATZ when it answers for every position; otherwise by lane from OpenDota.
        var roles = new List<HeroMeta>();
        if (Search is not null)
            for (int position = 1; position <= 5; position++)
            {
                var filter = new MetaFilter(saved.Patch, position, saved.Rank, saved.Sort, saved.MinPickRate);
                var (found, _) = await HeroMetaAsync(filter, DotaMetas.PostWith, cancellationToken);
                if (found is null)
                    break;
                roles.Add(found with { Filter = filter, Heroes = DotaMetas.Sorted(found.Heroes, saved.Sort, saved.MinPickRate) });
            }
        if (roles.Count == 5)
        {
            foreach (var publisher in publishers)
                if (await publisher.PublishRolesAsync(command.GuildId, channel, roles, saved.Count, cancellationToken) is { } failed)
                    return Result.Failure<bool>(new ErrorDescriptor("Features.PostFailed", failed));
        }
        else
        {
            var (meta, error) = await MetaAsync(DotaMetas.PostWith, cancellationToken);
            if (meta is null)
                return Result.Failure<bool>(new ErrorDescriptor("Features.MetaFailed", error ?? "OpenDota didn't answer."));
            foreach (var publisher in publishers)
                if (await publisher.PublishAsync(command.GuildId, channel, meta, saved.Count, cancellationToken) is { } failed)
                    return Result.Failure<bool>(new ErrorDescriptor("Features.PostFailed", failed));
        }
        await store.SaveDotaMetaAsync(saved with { LastPostedAt = clock.UtcNow });
        return true;
    }

    /// <summary>A STRATZ search, cached per filter for <paramref name="maxAge"/>; on failure the older cache, if any, with the reason.</summary>
    private async Task<(HeroMeta? Meta, string? Error)> HeroMetaAsync(MetaFilter filter, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        string key = DotaMetas.SearchKey(filter);
        HeroMeta? old = null;
        if (await store.GetCachedAsync(key) is { } hit)
        {
            try
            {
                old = JsonSerializer.Deserialize<HeroMeta>(hit.Payload, GameStats.Json);
            }
            catch (JsonException)
            {
                old = null;
            }
            if (old is not null && clock.UtcNow - hit.FetchedAt < maxAge)
                return (old, null);
        }
        if (Search is not { } search)
            return (old, "Stratz__ApiKey isn't set on the server.");
        var fetched = await search.GetHeroMetaAsync(filter with { Sort = MetaSort.Score }, cancellationToken);
        if (fetched is { Status: StatsStatus.Ok, Profile: { } meta })
        {
            await store.SaveCachedAsync(key, JsonSerializer.Serialize(meta, GameStats.Json), clock.UtcNow);
            return (meta, null);
        }
        return (old, fetched.Reason ?? "STRATZ didn't answer.");
    }

    public async Task<Result<int>> Handle(RunDotaMetaScheduleCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        int posted = 0;
        foreach (ulong guildId in command.GuildIds)
        {
            var saved = await store.GetDotaMetaAsync(guildId);
            if (saved.Schedule == MetaSchedule.Off || saved.ChannelId is null)
                continue;
            var zone = LocalTimes.FindZone((await settings.GetAsync(guildId, cancellationToken))?.TimeZoneId ?? command.DefaultTimeZone)
                ?? LocalTimes.FindZone(command.DefaultTimeZone) ?? TimeZoneInfo.Utc;
            var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
            DateOnly? last = saved.LastPostedAt is { } at ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime) : null;
            if (!DotaMetas.IsDue(saved, DateOnly.FromDateTime(local), local.Hour, last))
                continue;
            // Marked first, so a failing channel doesn't retry every hour; the next due day tries again.
            await store.SaveDotaMetaAsync(saved with { LastPostedAt = now });
            if ((await Handle(new PostDotaMetaCommand(guildId), cancellationToken)).IsSuccess)
                posted++;
        }
        return posted;
    }

    /// <summary>The cached meta when it is younger than <paramref name="maxAge"/>, otherwise a fresh one (or the cache when OpenDota fails).</summary>
    private async Task<(DotaMeta? Meta, string? Error)> MetaAsync(TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var cached = await store.GetCachedAsync(DotaMetas.CacheKey);
        DotaMeta? old = null;
        if (cached is { } hit)
        {
            try
            {
                old = JsonSerializer.Deserialize<DotaMeta>(hit.Payload, GameStats.Json);
            }
            catch (JsonException)
            {
                old = null;
            }
            if (old is not null && clock.UtcNow - hit.FetchedAt < maxAge)
                return (old, null);
        }
        var fetched = await source.GetMetaAsync(cancellationToken);
        if (fetched is { Status: StatsStatus.Ok, Profile: { } meta })
        {
            await store.SaveCachedAsync(DotaMetas.CacheKey, JsonSerializer.Serialize(meta, GameStats.Json), meta.FetchedAt);
            return (meta, null);
        }
        return (old, fetched.Reason ?? "OpenDota didn't answer.");
    }
}
