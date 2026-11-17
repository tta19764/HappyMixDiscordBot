using HappyMix.Application.Abstractions.Events;
using HappyMix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SeriesParticipationMigrationTests
{
    [Fact]
    public async Task UpgradeCarriesForwardAcceptanceAndPreservesDateWithdrawals()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IEventStore>();
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var date = new DateOnly(2026, 11, 2);
        var item = new GuildEvent(id, 1, 10, null, "Series", null, "UTC", date, new TimeOnly(10, 0), 60,
            EventRecurrence.Daily, [], date.AddDays(2), now, null);
        await store.CreateAsync(item, [20], Enumerable.Range(0, 3).Select(index => new EventOccurrence(id,
            date.AddDays(index), now.AddDays(index + 1), now.AddDays(index + 1).AddHours(1), OccurrenceStatus.Pending, null, null)).ToArray());
        await store.RespondAsync(id, date, 20, true, now);
        await store.RespondAsync(id, date.AddDays(1), 20, false, now.AddMinutes(1));
        await using (var context = await database.Services.GetRequiredService<IDbContextFactory<HappyMixDbContext>>().CreateDbContextAsync())
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261121000100_AddGameNews");
            await migrator.MigrateAsync();
        }
        store = (await database.OpenAsync()).GetRequiredService<IEventStore>();
        var loaded = (await store.GetAsync(1, id))!;
        Assert.True(loaded.Responses.Single(answer => answer.Date == date.AddDays(2)).Accepted);
        Assert.True(loaded.Responses.Single(answer => answer.Date == date.AddDays(1)).Withdrawn);
        await store.RespondAsync(id, date, 20, true, now.AddMinutes(2));
        Assert.False((await store.GetAsync(1, id))!.Responses.Single(answer => answer.Date == date.AddDays(1)).Accepted);
    }
}
