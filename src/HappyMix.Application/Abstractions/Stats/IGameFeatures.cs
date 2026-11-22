namespace HappyMix.Application.Abstractions.Stats;

/// <summary>Dota 2 lanes as OpenDota's lane roles number them (jungle, 4, is left out).</summary>
public enum DotaLane { Safe = 1, Mid = 2, Off = 3 }

public sealed record MetaHero(int HeroId, string Name, string? Image, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : Wins * 100.0 / Games;
}

/// <summary>The strongest heroes per lane in recent public matches, from OpenDota.</summary>
public sealed record DotaMeta(DateTimeOffset FetchedAt, IReadOnlyDictionary<DotaLane, IReadOnlyList<MetaHero>> Lanes);

/// <summary>How often the meta is posted: never, every day, every other day or once a week on a chosen day.</summary>
public enum MetaSchedule { Off, Daily, EveryTwoDays, Weekly }

/// <param name="Hour">The local hour (server zone) from which a due post goes out.</param>
/// <param name="Day">The weekday for <see cref="MetaSchedule.Weekly"/>.</param>
/// <param name="Count">Heroes shown and posted per lane or role.</param>
/// <param name="Rank">The rank group the scheduled post uses (STRATZ).</param>
/// <param name="Patch">Whether the post counts the current patch or the last 7 days (STRATZ).</param>
/// <param name="Sort">How the post orders heroes (STRATZ).</param>
public sealed record DotaMetaSettings(ulong GuildId, MetaSchedule Schedule, ulong? ChannelId, DayOfWeek Day, int Hour, DateTimeOffset? LastPostedAt,
    int Count = 10, MetaRank Rank = MetaRank.All, MetaPatch Patch = MetaPatch.Current, MetaSort Sort = MetaSort.Score)
{
    public static DotaMetaSettings Default(ulong guildId) => new(guildId, MetaSchedule.Off, null, DayOfWeek.Monday, 12, null);
}

/// <summary>Per-server game feature settings and the shared meta cache.</summary>
public interface IGameFeatureStore
{
    Task<DotaMetaSettings> GetDotaMetaAsync(ulong guildId);
    Task SaveDotaMetaAsync(DotaMetaSettings settings);
    Task<(string Payload, DateTimeOffset FetchedAt)?> GetCachedAsync(string key);
    Task SaveCachedAsync(string key, string payload, DateTimeOffset fetchedAt);
}

public interface IDotaMetaSource
{
    Task<StatsFetch<DotaMeta>> GetMetaAsync(CancellationToken cancellationToken);
}

/// <summary>Posts the meta in a server's channel. Returns null when posted, or why it wasn't.</summary>
public interface IDotaMetaPublisher
{
    /// <param name="count">Heroes per lane, from the server's settings.</param>
    Task<string?> PublishAsync(ulong guildId, ulong channelId, DotaMeta meta, int count, CancellationToken cancellationToken);

    /// <summary>The meta by role (positions 1–5) from STRATZ, with the server's filters; one column per role.</summary>
    Task<string?> PublishRolesAsync(ulong guildId, ulong channelId, IReadOnlyList<HeroMeta> roles, int count, CancellationToken cancellationToken);
}
