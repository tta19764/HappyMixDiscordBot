using HappyMix.Application.Abstractions.Guilds;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

public sealed class SqliteGuildSettingsTests
{
    [Fact]
    public async Task CatalogIsSeededOnSaveAndTeamAdditionsStayInTheirGuild()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var seeded = await database.OpenAsync(overrides: new Dictionary<string, string?>
        {
            ["PandaScore:CounterStrikeTeamAliases:NAVI"] = "3216",
            ["PandaScore:Dota2TeamAliases:NAVI"] = "1699"
        });
        var store = seeded.GetRequiredService<IGuildSettingsStore>();
        await store.SaveAsync(new GuildSettings(1, 10, "UTC"));
        await store.SaveAsync(new GuildSettings(2, 20, "UTC"));
        await store.UpdateAsync(1, "UTC", settings =>
        {
            var preferences = settings.Preferences!;
            var aliases = new Dictionary<string, long>(preferences.CounterStrikeTeamAliases!)
            {
                ["G2"] = 3210
            };
            return settings with { Preferences = preferences with { CounterStrikeTeamAliases = aliases } };
        });

        var reopened = await database.OpenAsync();
        var persisted = reopened.GetRequiredService<IGuildSettingsStore>();
        var first = (await persisted.GetAsync(1))!.Preferences!;
        var second = (await persisted.GetAsync(2))!.Preferences!;
        Assert.Equal(3216L, first.CounterStrikeTeamAliases!["NAVI"]);
        Assert.Equal(3210L, first.CounterStrikeTeamAliases["G2"]);
        Assert.Equal(1699L, first.Dota2TeamAliases!["NAVI"]);
        Assert.False(second.CounterStrikeTeamAliases!.ContainsKey("G2"));
    }

    [Fact]
    public async Task ChannelUpdatesPreserveSavedPreferencesAcrossRestart()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IGuildSettingsStore>();
        var preferences = new GuildEsportsPreferences([3210], [], EsportsNotifications.Reminders,
            45, [100], 101, 102);
        await store.SaveAsync(new GuildSettings(1, 20, "Europe/Kyiv", preferences));
        await store.UpdateAsync(1, "UTC", settings => settings with { ChannelId = 30 });

        var reopened = await database.OpenAsync();
        var saved = await reopened.GetRequiredService<IGuildSettingsStore>().GetAsync(1);
        Assert.NotNull(saved);
        Assert.Equal(30UL, saved.ChannelId);
        Assert.Equal("Europe/Kyiv", saved.TimeZoneId);
        Assert.NotNull(saved.Preferences);
        Assert.Equal([3210L], saved.Preferences.CounterStrikeTeamIds!);
        Assert.Empty(saved.Preferences.Dota2TeamIds!);
        Assert.Equal(EsportsNotifications.Reminders, saved.Preferences.Notifications);
        Assert.Equal(45, saved.Preferences.ReminderMinutes);
        Assert.Equal([100UL], saved.Preferences.OrganizerRoleIds!);
        Assert.Equal(101UL, saved.Preferences.CounterStrikePingRoleId);
        Assert.Equal(102UL, saved.Preferences.Dota2PingRoleId);
    }

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
