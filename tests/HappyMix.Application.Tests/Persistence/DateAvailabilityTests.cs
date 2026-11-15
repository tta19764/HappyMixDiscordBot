using HappyMix.Application.Abstractions.Guilds;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class DateAvailabilityTests
{
    [Fact]
    public async Task DateRangeIsAtomicAndCanBeRetried()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IAvailabilityStore>();
        var first = new DateTimeOffset(2030, 6, 1, 18, 0, 0, TimeSpan.Zero);
        var windows = Enumerable.Range(0, 3).Select(index => new AvailabilityWindow(Guid.NewGuid(),
            first.AddDays(index), first.AddDays(index).AddHours(2))).ToArray();
        await store.SaveAsync(1, 2, null, first.AddDays(2).AddHours(1), first.AddDays(2).AddHours(3));
        await Assert.ThrowsAsync<AvailabilityException>(() => store.SaveDatesAsync(1, 2, windows));
        Assert.Single(await store.ListAsync(1, 2));
        await store.SaveDatesAsync(1, 3, windows);
        await store.SaveDatesAsync(1, 3, windows.Select(window => window with { Id = Guid.NewGuid(), Tentative = true }).ToArray());
        var saved = await store.ListAsync(1, 3);
        Assert.Equal(3, saved.Count);
        Assert.All(saved, window => Assert.True(window.Tentative));
    }
}
