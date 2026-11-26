using System.Reflection;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

/// <summary>The per-server cache in front of account links, groups and server settings.</summary>
public sealed class CachedStoreTests
{
    private const ulong Guild = 7;

    private static GuildCache NewCache() =>
        new(new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>());

    [Fact]
    public async Task ReadsAreServedFromTheCacheUntilAWrite()
    {
        var cache = NewCache();
        var (inner, calls) = Counting<IGuildGroupStore>();
        var store = new CachedGuildGroupStore(inner, cache);
        await store.ListAsync(Guild);
        await store.ListAsync(Guild);
        await store.ListAsync(Guild + 1);
        Assert.Equal(2, calls.Count(call => call == nameof(IGuildGroupStore.ListAsync)));

        await store.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), Guid.NewGuid(), "Dota 2"));
        await store.ListAsync(Guild);
        await store.ListAsync(Guild + 1); // another server's cache stays
        Assert.Equal(3, calls.Count(call => call == nameof(IGuildGroupStore.ListAsync)));
    }

    /// <summary>Every method that isn't a read must clear the cache, so a new write method can't forget to.</summary>
    [Theory]
    [InlineData(typeof(IAccountLinkStore))]
    [InlineData(typeof(IGuildGroupStore))]
    [InlineData(typeof(IGuildSettingsStore))]
    public async Task EveryWriteClearsTheServersCache(Type contract)
    {
        var cachedReads = new Dictionary<Type, string>
        {
            [typeof(IAccountLinkStore)] = nameof(IAccountLinkStore.ListAsync),
            [typeof(IGuildGroupStore)] = nameof(IGuildGroupStore.ListAsync),
            [typeof(IGuildSettingsStore)] = nameof(IGuildSettingsStore.GetAsync)
        };
        foreach (var method in contract.GetMethods().Where(method => !method.Name.StartsWith("Get", StringComparison.Ordinal)
            && !method.Name.StartsWith("List", StringComparison.Ordinal)))
        {
            var cache = NewCache();
            var (inner, calls, store) = Wrap(contract, cache);
            var read = contract.GetMethod(cachedReads[contract])!;
            await Call(store, read);
            await Call(store, read);
            Assert.Equal(1, calls.Count(call => call == read.Name));
            await Call(store, method);
            Assert.Contains(method.Name, calls);
            await Call(store, read);
            Assert.True(calls.Count(call => call == read.Name) == 2, $"{contract.Name}.{method.Name} doesn't clear the cache.");
        }
    }

    [Fact]
    public async Task AReadThatStartedBeforeAWriteIsNotServedAfterIt()
    {
        var cache = NewCache();
        var release = new TaskCompletionSource();
        int loads = 0;
        async Task<IReadOnlyList<AccountLink>> Load(CancellationToken _)
        {
            int load = Interlocked.Increment(ref loads);
            if (load == 1)
                await release.Task; // the first read is slow and sees the data before the write
            return load == 1 ? [] : [new AccountLink(1, 2, DateTimeOffset.UnixEpoch)];
        }

        var slow = cache.GetAsync(Guild, "links", Load);
        await cache.EvictAsync(Guild); // a write commits while the first read is still loading
        release.SetResult();
        Assert.Empty(await slow);
        Assert.Single(await cache.GetAsync(Guild, "links", Load));
        Assert.Equal(2, loads);
    }

    private static (object Inner, List<string> Calls, object Store) Wrap(Type contract, GuildCache cache)
    {
        var method = typeof(CachedStoreTests).GetMethod(nameof(Counting), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(contract);
        object pair = method.Invoke(null, null)!;
        object inner = pair.GetType().GetField("Item1")!.GetValue(pair)!;
        var calls = (List<string>)pair.GetType().GetField("Item2")!.GetValue(pair)!;
        object store = contract == typeof(IAccountLinkStore) ? new CachedAccountLinkStore((IAccountLinkStore)inner, cache)
            : contract == typeof(IGuildGroupStore) ? new CachedGuildGroupStore((IGuildGroupStore)inner, cache)
            : new CachedGuildSettingsStore((IGuildSettingsStore)inner, cache);
        return (inner, calls, store);
    }

    /// <summary>Calls a store method for <see cref="Guild"/> with default values for every other argument.</summary>
    private static async Task Call(object store, MethodInfo method)
    {
        var arguments = method.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(ulong) && parameter.Position == 0 ? Guild
            : parameter.ParameterType == typeof(GuildSettings) ? new GuildSettings(Guild, 1, "UTC")
            : parameter.ParameterType == typeof(string) ? "x"
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
            : null).ToArray();
        await (Task)method.Invoke(store, arguments)!;
    }

    private static (T Inner, List<string> Calls) Counting<T>() where T : class
    {
        var calls = new List<string>();
        var proxy = DispatchProxy.Create<T, CountingProxy>();
        ((CountingProxy)(object)proxy).Calls = calls;
        return (proxy, calls);
    }

    /// <summary>A store that records calls and returns empty results.</summary>
    public class CountingProxy : DispatchProxy
    {
        public List<string> Calls { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            var type = targetMethod.ReturnType;
            if (type == typeof(Task))
                return Task.CompletedTask;
            var result = type.GetGenericArguments()[0];
            object? value = result.IsGenericType && result.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                ? Array.CreateInstance(result.GetGenericArguments()[0], 0)
                : result.IsValueType ? Activator.CreateInstance(result) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
        }
    }
}
