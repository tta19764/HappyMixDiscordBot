using HappyMix.Application.Abstractions.Guilds;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SqliteGuildSettingsTests
{
    [Fact]
    public async Task SettingsSurviveRestartAndUpdatesStayWithinGuild()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IGuildSettingsStore>();
        await store.SaveAsync(new GuildSettings(ulong.MaxValue, 20, "Europe/Kyiv"));
        await store.SaveAsync(new GuildSettings(2, 30, "UTC"));

        var reopened = await database.OpenAsync();
        store = reopened.GetRequiredService<IGuildSettingsStore>();
        Assert.Equal(new GuildSettings(ulong.MaxValue, 20, "Europe/Kyiv"), await store.GetAsync(ulong.MaxValue));
        var updated = new GuildSettings(ulong.MaxValue, 40, "UTC");
        await store.SaveAsync(updated);

        Assert.Equal(updated, await store.GetAsync(ulong.MaxValue));
        Assert.Equal(new GuildSettings(2, 30, "UTC"), await store.GetAsync(2));
        Assert.Null(await store.GetAsync(3));
    }
}
