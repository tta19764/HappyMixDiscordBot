using System.Text.Json;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Live;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Guilds;
using HappyMix.Application.Live;
using HappyMix.Application.Players;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Bot.Services;
using HappyMix.Infrastructure.Steam;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Players;

/// <summary>Live: opt-in sharing, who gets messages, which games matter and how often.</summary>
public sealed class LiveTests
{
    private const ulong Guild = 1, Kai = 10, Mira = 20, Taro = 30, Outsider = 40;
    private const string KaiSteam = "76561198000000001";

    [Fact]
    public async Task OnlyMembersShareAndOnlyOrganizersPostInTheChannel()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();

        var live = (await sender.Send(new SetLiveSharingCommand(Actor(Mira), groupId, Share: true, Notify: false))).Value;
        Assert.Equal((true, false, 1), (live.Share, live.Notify, live.Sharers));
        Assert.True((await sender.Send(new SetLiveSharingCommand(Actor(Outsider), groupId, true, true))).IsFailure);
        Assert.Equal("Live.Forbidden", (await sender.Send(new SetLiveChannelCommand(Actor(Mira), groupId, true))).Error.Code);
        Assert.True((await sender.Send(new SetLiveChannelCommand(Actor(Kai), groupId, true))).Value.PostInChannel);
    }

    [Fact]
    public async Task StartingAGameTheGroupCaresAboutMessagesMembersOnceInTheCooldown()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        await sender.Send(new SetLiveSharingCommand(Actor(Kai), groupId, Share: true, Notify: false));
        await sender.Send(new SetLiveSharingCommand(Actor(Mira), groupId, Share: false, Notify: true));
        await sender.Send(new AddPlayerGameCommand(Taro, "Helldivers 2", null, null, null, GamePreference.Want));

        // A game nobody in the group plays or wants: recorded, no message.
        Assert.Equal(0, (await sender.Send(new ReportPlayingCommand(Kai, "Solitaire", "discord"))).Value);
        // The group's own game: Mira, who asked for messages, gets one.
        Assert.Equal(1, (await sender.Send(new ReportPlayingCommand(Kai, "Dota 2", "discord"))).Value);
        var notice = Assert.Single(fixture.Notices);
        Assert.Equal((Kai, "Dota 2", "Squad", false), (notice.PlayerId, notice.Game, notice.GroupName, notice.PostInChannel));
        Assert.Equal([Mira], notice.DmRecipients);

        // Repeats of the open session, and restarting inside the cooldown, stay quiet.
        Assert.Equal(0, (await sender.Send(new ReportPlayingCommand(Kai, "dota 2", "discord"))).Value);
        await sender.Send(new ReportPlayingCommand(Kai, null, "discord"));
        Assert.Equal(0, (await sender.Send(new ReportPlayingCommand(Kai, "Dota 2", "discord"))).Value);
        fixture.Clock.Advance(LiveRules.Cooldown);
        await sender.Send(new ReportPlayingCommand(Kai, null, "discord"));
        Assert.Equal(1, (await sender.Send(new ReportPlayingCommand(Kai, "Dota 2", "discord"))).Value);

        // A game another member wants counts too, and names them.
        Assert.Equal(1, (await sender.Send(new ReportPlayingCommand(Kai, "Helldivers 2", "discord"))).Value);
        Assert.Equal([Taro], fixture.Notices[^1].AlsoWant);

        var view = (await sender.Send(new GetGroupLiveQuery(Actor(Mira), groupId))).Value;
        Assert.Equal(("Helldivers 2", Kai), (Assert.Single(view.Playing).Game, view.Playing[0].UserId));
        Assert.Contains(view.Recent, session => session.Game == "Dota 2");

        // Someone who doesn't share is never reported, whatever they play.
        Assert.Equal(0, (await sender.Send(new ReportPlayingCommand(Mira, "Dota 2", "discord"))).Value);
    }

    [Fact]
    public async Task SteamIsAskedOnlyAboutVerifiedAccountsOfSharers()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;
        Guid groupId = await fixture.CreateGroupAsync();
        await sender.Send(new SetLiveSharingCommand(Actor(Kai), groupId, Share: true, Notify: false));
        await sender.Send(new SetLiveSharingCommand(Actor(Mira), groupId, Share: false, Notify: true));
        fixture.Steam.Playing[KaiSteam] = "Dota 2";

        await sender.Send(new LinkSteamCommand(Kai, KaiSteam)); // pasted, not proven
        Assert.Equal(0, (await sender.Send(new SyncSteamPlayingCommand())).Value);
        await sender.Send(new LinkDiscordSteamCommand(Kai, KaiSteam, DiscordVerified: true));
        Assert.Equal(1, (await sender.Send(new SyncSteamPlayingCommand())).Value);
        Assert.Equal("Dota 2", Assert.Single((await sender.Send(new GetGroupLiveQuery(Actor(Mira), groupId))).Value.Playing).Game);

        fixture.Steam.Playing.Clear();
        await sender.Send(new SyncSteamPlayingCommand());
        Assert.Empty((await sender.Send(new GetGroupLiveQuery(Actor(Mira), groupId))).Value.Playing);
    }

    [Fact]
    public void SteamStatusesAndNoticeTextAreRead()
    {
        using var summaries = JsonDocument.Parse($$$"""
            {"response":{"players":[{"steamid":"{{{KaiSteam}}}","gameextrainfo":"Dota 2"},{"steamid":"76561198000000002"},{"steamid":"bad","gameextrainfo":"X"}]}}
            """);
        Assert.Equal([(KaiSteam, "Dota 2")], SteamDirectory.ParsePlaying(summaries.RootElement).ToArray());

        var notice = new LiveNotice(Guild, Guid.NewGuid(), "Squad", Kai, "Dota_2", [Taro], [Mira], false);
        string text = DiscordLiveNotifier.Text(notice, "en");
        Assert.Contains("<@10>", text, StringComparison.Ordinal);
        Assert.Contains("Dota\\_2", text, StringComparison.Ordinal);
        Assert.Contains("<@30>", text, StringComparison.Ordinal);
    }

    private static GroupActor Actor(ulong user) => new(Guild, user, false);

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public PlayerGameTests.FakeSteam Steam { get; } = new();
        public MutableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
        public List<LiveNotice> Notices { get; } = [];
        public ISender Sender => application.GetRequiredService<ISender>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton(storage.GetRequiredService<IPlayerGameStore>())
                .AddSingleton(storage.GetRequiredService<ILiveStore>())
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton<ISteamDirectory>(fixture.Steam)
                .AddSingleton<ILiveNotifier>(new Recorder(fixture.Notices))
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        /// <summary>"Squad": Kai organizes, Mira and Taro are members, Dota 2 is its game.</summary>
        public async Task<Guid> CreateGroupAsync()
        {
            var groups = database.Services.GetRequiredService<IGuildGroupStore>();
            await groups.UpdateAsync(Guild, "Squad", _ => new GuildGroup("Squad", Kai, [Kai, Mira, Taro], []));
            var groupId = (await groups.ListAsync(Guild)).Single().Id;
            await groups.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), groupId, "Dota 2", GroupGame.SteamProvider, "570"));
            return groupId;
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }

    private sealed class Recorder(List<LiveNotice> notices) : ILiveNotifier
    {
        public Task NotifyAsync(LiveNotice notice, CancellationToken cancellationToken)
        {
            notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    internal sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private DateTimeOffset current = now;
        public DateTimeOffset UtcNow => current;
        public void Advance(TimeSpan by) => current += by;
    }
}
