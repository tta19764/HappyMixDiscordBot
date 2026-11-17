using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Bot.Interactions;
using HappyMix.Bot.Jobs;
using HappyMix.Bot.Services;
using HappyMix.Bot.Web;
using HappyMix.Domain.Esports;
using HappyMix.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests;

/// <summary>v0.0.4: community defaults, optional settings, announcement routing and channel notifications.</summary>
public sealed class QualityOfLifeV004Tests
{
    private static readonly CommunityDefaults Ukrainian = CommunityDefaults.Ukrainian;

    [Fact]
    public void UnconfiguredServersUseUkrainianDefaultsButExplicitChoicesStay()
    {
        var fresh = GuildEsportsPreferences.Default;
        Assert.Equal("uk", fresh.LanguageFor(Esport.CounterStrike2, Ukrainian));
        var discovery = fresh.DiscoveryFor(Esport.Dota2, Ukrainian);
        Assert.False(discovery.Enabled);
        Assert.True(discovery.CommunityLists);
        Assert.Equal("uk", discovery.Languages);
        Assert.Contains("Ukraine", discovery.Countries, StringComparison.Ordinal);
        Assert.Contains("Ukrainian", discovery.Tags, StringComparison.Ordinal);
        Assert.True(discovery.IsValid);

        var chosen = fresh with { CounterStrikeLanguage = "en", Dota2Discovery = new(true, "pl", "", "", "viewers") };
        Assert.Equal("en", chosen.LanguageFor(Esport.CounterStrike2, Ukrainian));
        Assert.Equal("uk", chosen.LanguageFor(Esport.Dota2, Ukrainian));
        Assert.Equal("pl", chosen.DiscoveryFor(Esport.Dota2, Ukrainian).Languages);
    }

    [Fact]
    public void SettingsSavedBeforeLanguagesExistedFollowTheCommunityLanguage()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<GuildEsportsPreferences>("""{"ReminderMinutes":20}""")!;
        Assert.Null(legacy.CounterStrikeLanguage);
        Assert.Equal("uk", legacy.LanguageFor(Esport.CounterStrike2, Ukrainian));
        var v002 = System.Text.Json.JsonSerializer.Deserialize<GuildEsportsPreferences>("""{"CounterStrikeLanguage":"en"}""")!;
        Assert.Equal("en", v002.LanguageFor(Esport.CounterStrike2, Ukrainian));
    }

    [Fact]
    public void CommunityListsFollowTheDefaultsWithTheServersOwnSwitchAndRanking()
    {
        var saved = GuildEsportsPreferences.Default with
        {
            CounterStrikeDiscovery = new StreamDiscoveryPreferences(true, "xx", "", "", "viewers,language", CommunityLists: true)
        };
        var effective = saved.DiscoveryFor(Esport.CounterStrike2, Ukrainian);
        Assert.True(effective.Enabled);
        Assert.Equal("uk", effective.Languages);
        Assert.Equal("viewers,language", effective.Priority);
    }

    [Fact]
    public void OmittedCommandOptionsKeepTheirValues()
    {
        var current = new StreamDiscoveryPreferences(true, "uk,en", "Ukraine", "Ukrainian", "language,viewers");
        Assert.Equal(current, current.Merge(null, null, null, null, null));
        Assert.Equal(current with { Enabled = false }, current.Merge(false, null, null, null, null));

        var tags = current.Merge(null, null, null, "#UA, ukrainian, Ukrainian", null);
        Assert.Equal("uk,en", tags.Languages);
        Assert.Equal("Ukraine", tags.Countries);
        Assert.Equal("UA,ukrainian", tags.Tags);
        Assert.Equal("", current.Merge(null, null, "-", null, null).Countries);
        Assert.Equal(StreamDiscoveryPreferences.DefaultPriority, current.Merge(null, null, null, null, "default").Priority);
        Assert.True(current.Merge(null, "default", null, null, null).CommunityLists);
    }

    [Fact]
    public void TypingOneListLeavesCommunityListsAndKeepsTheOthers()
    {
        var community = Ukrainian.Discovery(enabled: true);
        var custom = community.Merge(null, "uk, EN", null, null, null);
        Assert.False(custom.CommunityLists);
        Assert.Equal("uk,en", custom.Languages);
        Assert.Equal(community.Countries, custom.Countries);
        Assert.True(custom.Enabled);
    }

    [Theory]
    [InlineData("1234567890123456789012345678")]
    [InlineData("a,b,c,d,e,f,g,h,i,j,k")]
    public void RejectsOverlongTagsAndTooManyValues(string tags) =>
        Assert.False(new StreamDiscoveryPreferences(Tags: tags).IsValid);

    [Fact]
    public void UntouchedDashboardDiscoveryIsNotSaved()
    {
        Assert.Null(new GameSettingsForm().Discovery());
        var enabled = new GameSettingsForm { DiscoverStreams = true }.Discovery();
        Assert.True(enabled!.CommunityLists);
        Assert.True(enabled.Enabled);
        var custom = new GameSettingsForm { StreamListsMode = GameSettingsForm.Custom, StreamLanguages = "UK, en", StreamPriority = "viewers" }.Discovery();
        Assert.Equal("uk,en", custom!.Languages);
        Assert.False(custom.CommunityLists);
        Assert.Equal("viewers", custom.Priority);
    }

    [Theory]
    [InlineData("cs2-tournaments", Esport.CounterStrike2)]
    [InlineData("#cs-news", Esport.CounterStrike2)]
    [InlineData("counter-strike", Esport.CounterStrike2)]
    [InlineData("dota2-announcements", Esport.Dota2)]
    [InlineData("general", null)]
    [InlineData("esports", null)]
    [InlineData("cs2-and-dota", null)]
    public void ChannelNamesThatPointAtAGameAreRecognized(string name, Esport? expected) =>
        Assert.Equal(expected, DashboardController.GameNamed(name));

    [Fact]
    public void RoutesGroupGamesByChannel()
    {
        var routes = (GuildEsportsPreferences.Default with { Dota2ChannelId = 9 }).Routes(5);
        Assert.Equal([Esport.CounterStrike2], routes.Single(route => route.ChannelId == 5).Games);
        Assert.Equal([Esport.Dota2], routes.Single(route => route.ChannelId == 9).Games);
        Assert.Single(GuildEsportsPreferences.Default.Routes(5));
        Assert.Empty(GuildEsportsPreferences.Default.Routes(0));
    }

    [Theory]
    [InlineData("group", "Thursday Scrims!", "group-thursday-scrims")]
    [InlineData("event", "  Турнір  №1 ", "event-турнір-no1")]
    [InlineData("event", "!!!", "event-00000000")]
    public void CreatedChannelsAreNamedAfterTheGroupOrEvent(string scope, string title, string expected) =>
        Assert.Equal(expected, EventNotificationSettings.ChannelName(scope, title, Guid.Empty));

    [Fact]
    public void NotifyTargetsAcceptAutocompleteValuesAndBareEventIds()
    {
        var id = Guid.NewGuid();
        Assert.True(NotificationSettingsModule.TryParse($"group:{id}", out string scope, out var parsed));
        Assert.Equal(("group", id), (scope, parsed));
        Assert.True(NotificationSettingsModule.TryParse(id.ToString(), out scope, out parsed));
        Assert.Equal(("event", id), (scope, parsed));
        Assert.False(NotificationSettingsModule.TryParse("team:abc", out _, out _));
    }

    [Fact]
    public void ChannelPostsAreLocalizedAndCarryAShortStableReference()
    {
        var start = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
        var guildEvent = new GuildEvent(Guid.NewGuid(), 1, 10, null, "Scrims", null, "UTC", new DateOnly(2026, 11, 20),
            new TimeOnly(18, 0), 60, EventRecurrence.Once, [], null, start.AddDays(-1), null);
        var occurrence = new EventOccurrence(guildEvent.Id, new DateOnly(2026, 11, 20), start, start.AddHours(1), OccurrenceStatus.Confirmed, start, null);
        var reminder = PublishEventChannelsJob.BuildReminder("uk", guildEvent, occurrence, 4, "event-reminder/key", "https://example.test/e");
        Assert.Equal("⏰ Scrims скоро почнеться", reminder.Title);
        Assert.Equal("4", reminder.Fields.Single().Value);
        Assert.Equal("Нагадування HappyMix · " + PublishEventChannelsJob.Reference("event-reminder/key"), reminder.Footer!.Value.Text);
        Assert.DoesNotContain("event-reminder", reminder.Footer!.Value.Text, StringComparison.Ordinal);
        Assert.Equal(PublishEventChannelsJob.Reference("a"), PublishEventChannelsJob.Reference("a"));
        Assert.NotEqual(PublishEventChannelsJob.Reference("a"), PublishEventChannelsJob.Reference("b"));

        var canceled = PublishEventChannelsJob.BuildUpdate("en", "series-canceled", guildEvent, null, 0, null).Build();
        Assert.Equal("❌ Scrims was canceled", canceled.Title);
        Assert.Equal("<@10> canceled every remaining date.", canceled.Description);
    }

    [Fact]
    public async Task RouteOptionsPersistAndUpgradedRoutesKeepReminderOnlyBehavior()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var target = Guid.NewGuid();
        await using (var context = await database.Services.GetRequiredService<IDbContextFactory<HappyMixDbContext>>().CreateDbContextAsync())
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261122000100_AddSeriesParticipation");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO NotificationRoutes VALUES ('1', 'group', {target.ToString()}, 1, '55', 'uk')");
            await migrator.MigrateAsync();
        }
        var routes = (await database.OpenAsync()).GetRequiredService<NotificationRouteStore>();
        var upgraded = (await routes.GetAsync(1, "group", target))!;
        Assert.Equal((true, 55UL, (int?)null, false), (upgraded.Enabled, upgraded.Channel(), upgraded.ReminderMinutes, upgraded.Updates));

        await routes.SaveAsync(upgraded with { ReminderMinutes = 45, Updates = true });
        var saved = (await (await database.OpenAsync()).GetRequiredService<NotificationRouteStore>().GetAsync(1, "group", target))!;
        Assert.Equal((45, true), (saved.ReminderMinutes, saved.Updates));
    }

    [Fact]
    public async Task ChangedEventsIncludeNewConfirmedAndCanceledOnly()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var store = database.Services.GetRequiredService<IEventStore>();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var old = await CreateAsync(store, "Old", now.AddDays(-3), now);
        var fresh = await CreateAsync(store, "Fresh", now.AddMinutes(-10), now);
        var confirmed = await CreateAsync(store, "Confirmed", now.AddDays(-3), now);
        await store.SetStatusAsync(confirmed, new DateOnly(2026, 11, 18), [OccurrenceStatus.Pending], OccurrenceStatus.Confirmed, now.AddMinutes(-5));

        var changed = (await store.ListChangedAsync(1, now.AddHours(-1))).Select(item => item.Event.Title).Order().ToArray();
        Assert.Equal(["Confirmed", "Fresh"], changed);
        Assert.DoesNotContain(old, (await store.ListChangedAsync(1, now.AddHours(-1))).Select(item => item.Event.Id));
    }

    private static async Task<Guid> CreateAsync(IEventStore store, string title, DateTimeOffset created, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var date = new DateOnly(2026, 11, 18);
        await store.CreateAsync(new GuildEvent(id, 1, 10, null, title, null, "UTC", date, new TimeOnly(18, 0), 60,
            EventRecurrence.Once, [], null, created, null), [20],
            [new EventOccurrence(id, date, now.AddDays(1), now.AddDays(1).AddHours(1), OccurrenceStatus.Pending, null, null)]);
        return id;
    }
}
