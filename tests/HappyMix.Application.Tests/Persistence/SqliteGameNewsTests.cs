using HappyMix.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SqliteGameNewsTests
{
    [Fact]
    public async Task SubscriptionsAndDeliveryHistorySurviveRestartAndAreRemovedTogether()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<GameNewsStore>();
        var subscription = new GameNewsSubscription("123", 570, "Dota 2", "456", "789", "321", 100);
        await store.AddAsync(subscription, default);
        await store.RecordDeliveryAsync(subscription, "announcement", default);
        await store.RecordDeliveryAsync(subscription, "announcement", default);

        var reopened = await database.OpenAsync();
        store = reopened.GetRequiredService<GameNewsStore>();
        Assert.Equal(subscription, Assert.Single(await store.ListAsync(default)));
        Assert.Equal("announcement", Assert.Single(await store.DeliveredAsync(subscription, default)));

        await store.SetSourcesAsync(subscription, GameNewsSources.PatchNotes | GameNewsSources.Press, default);
        Assert.Equal(GameNewsSources.PatchNotes | GameNewsSources.Press, Assert.Single(await store.ListAsync(default)).Sources);

        await store.MarkDeletingAsync(subscription, default);
        Assert.True(Assert.Single(await store.ListAsync(default)).Deleting);
        await store.RemoveAsync(subscription, default);
        await store.AddAsync(subscription, default);
        Assert.Empty(await store.DeliveredAsync(subscription, default));
    }
}
