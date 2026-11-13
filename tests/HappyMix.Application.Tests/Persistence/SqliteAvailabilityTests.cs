using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Bot.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SqliteAvailabilityTests
{
    [Fact]
    public async Task OvernightWindowSurvivesReopenAndOnlyItsOwnerCanChangeIt()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IAvailabilityStore>();
        Assert.True(AvailabilityModule.TryParse("2026-11-13 22:00 +02:00", out var start));
        Assert.True(AvailabilityModule.TryParse("2026-11-14 01:00 +02:00", out var finish));
        Assert.False(AvailabilityModule.TryParse("2026-11-13 22:00", out _));
        var saved = await store.SaveAsync(1, 2, null, start, finish);

        var reopened = await database.OpenAsync();
        store = reopened.GetRequiredService<IAvailabilityStore>();
        Assert.Equal(saved, Assert.Single(await store.ListAsync(1, 2)));
        Assert.Empty(await store.ListAsync(2, 2));
        Assert.False(await store.RemoveAsync(1, 3, saved.Id));
        await Assert.ThrowsAsync<AvailabilityException>(() => store.SaveAsync(1, 3, saved.Id, start, finish));
        await Assert.ThrowsAsync<AvailabilityException>(() => store.SaveAsync(1, 2, null, finish, start));
        await Assert.ThrowsAsync<AvailabilityException>(() => store.SaveAsync(1, 2, null, start.AddHours(1), finish));
        var edited = await store.SaveAsync(1, 2, saved.Id, start, finish.AddHours(1));
        Assert.Equal(finish.AddHours(1), edited.End);
        Assert.True(await store.RemoveAsync(1, 2, saved.Id));
        Assert.Empty(await store.ListAsync(1, 2));
    }
}
