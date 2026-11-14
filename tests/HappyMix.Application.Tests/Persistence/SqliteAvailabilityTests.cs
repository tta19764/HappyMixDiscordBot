using HappyMix.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Bot.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SqliteAvailabilityTests
{
    [Fact]
    public async Task UpgradePreservesExistingRangesAsDefinite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new HappyMixDbContext(new DbContextOptionsBuilder<HappyMixDbContext>().UseSqlite(connection).Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261116000100_AddAccountLinks");
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AvailabilityWindows (Id, GuildId, UserId, StartUtc, EndUtc) VALUES ({id}, '1', '2', 100, 200)");
        await context.Database.ExecuteSqlRawAsync("INSERT INTO WeeklyAvailability (GuildId, UserId, Day, StartTicks, EndTicks, TimeZone) VALUES ('1', '2', 1, 100, 200, 'UTC')");
        await migrator.MigrateAsync();
        var window = Assert.Single(await context.Set<AvailabilityRecord>().ToListAsync());
        Assert.Equal(id, window.Id);
        Assert.Equal(100, window.StartUtc);
        Assert.False(window.Tentative);
        var weekly = Assert.Single(await context.Set<WeeklyAvailabilityRecord>().ToListAsync());
        Assert.Equal(100, weekly.StartTicks);
        Assert.False(weekly.Tentative);
        Assert.False(context.Database.HasPendingModelChanges());
    }

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
