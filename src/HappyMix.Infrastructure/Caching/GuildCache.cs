using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Caching.Hybrid;

namespace HappyMix.Infrastructure.Caching;

/// <summary>
/// In-memory cache for small per-server data that nearly every page and command reads (account links, groups, server
/// settings). Each server has a generation number that is part of every key and goes up after every write, so a value
/// read before a write can never be served after it, even when the read finishes later. Old generations are removed by
/// tag and expire on their own; <see cref="Lifetime"/> also covers writes that bypass the stores (manual SQL).
/// </summary>
internal sealed class GuildCache(HybridCache cache)
{
    /// <summary>The longest a value is kept, whatever happens.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    /// <summary>The generation of data that isn't per server, like the list of every server's settings.</summary>
    public const ulong AllServers = 0;

    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = Lifetime, LocalCacheExpiration = Lifetime };
    private readonly ConcurrentDictionary<ulong, long> generations = new();

    public static string Tag(ulong guildId) => "guild:" + guildId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The cached value of <paramref name="name"/> for the server, loading it once when missing.</summary>
    /// <remarks>Callers asking at once share one load, which gets the cache's token: it stops only when they all give up.</remarks>
    public async Task<T> GetAsync<T>(ulong guildId, string name, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken = default)
    {
        long generation = generations.GetValueOrDefault(guildId);
        string key = $"{name}:{guildId}:{generation}";
        var cached = await cache.GetOrCreateAsync(key, load, static async (loader, token) => new Cached<T>(await loader(token)), Entry, [Tag(guildId)],
            cancellationToken);
        return cached.Value;
    }

    /// <summary>Forgets everything cached for the server. Call after every write that may change what it reads.</summary>
    public async Task EvictAsync(ulong guildId)
    {
        generations.AddOrUpdate(guildId, 1, (_, current) => current + 1);
        await cache.RemoveByTagAsync(Tag(guildId));
    }

    /// <summary>Runs a write, then forgets the server's cached data, also when the write fails halfway.</summary>
    public async Task<T> WriteAsync<T>(ulong guildId, Func<Task<T>> write)
    {
        try
        {
            return await write();
        }
        finally
        {
            await EvictAsync(guildId);
        }
    }

    public Task WriteAsync(ulong guildId, Func<Task> write) => WriteAsync(guildId, async () =>
    {
        await write();
        return true;
    });
}

/// <summary>
/// A cached value. Marked immutable so the cache keeps this instance instead of copying it through JSON: cached
/// records are only ever replaced with <c>with</c>, never changed in place.
/// </summary>
[ImmutableObject(true)]
internal sealed class Cached<T>(T value)
{
    public T Value { get; } = value;
}
