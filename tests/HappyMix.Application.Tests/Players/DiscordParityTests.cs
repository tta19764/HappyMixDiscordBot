using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.News;
using HappyMix.Application.Abstractions.Players;
using HappyMix.Application.Guilds;
using HappyMix.Application.News;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Bot.Interactions;
using HappyMix.Bot.Services;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Players;

/// <summary>Features reachable from both Discord and the dashboard share the same commands.</summary>
public sealed class DiscordParityTests
{
    private const ulong Guild = 1, Kai = 10, Mira = 20, Stranger = 99;

    [Fact]
    public async Task GameNewsGoesThroughCommandsForBothDiscordAndTheDashboard()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;

        Assert.True((await sender.Send(new CreateGameNewsCommand(Guild, Kai, 0, "Nothing"))).IsFailure);
        Assert.True((await sender.Send(new CreateGameNewsCommand(Guild, Kai, 730, "CS2", GameNewsSources.None))).IsFailure);
        var created = (await sender.Send(new CreateGameNewsCommand(Guild, Kai, 730, " Counter-Strike 2 "))).Value;
        Assert.Equal(("Counter-Strike 2", "500", "600", GameNewsSources.Official), (created.Name, created.ChannelId, created.RoleId, created.Sources));
        Assert.Equal("This game already has a subscription.", (await sender.Send(new CreateGameNewsCommand(Guild, Mira, 730, "Again"))).Error.Description);

        Assert.True((await sender.Send(new SubscribeGameNewsCommand(Guild, Mira, 730, true))).IsSuccess);
        Assert.Equal((Mira, 600UL, true), fixture.Gateway.Roles.Last());
        Assert.True((await sender.Send(new SubscribeGameNewsCommand(Guild, Mira, 570, true))).IsFailure);

        // Turning press on records the press posts already out, so they aren't posted all at once.
        Assert.Equal("Only the subscription creator or a server administrator can choose its news.",
            (await sender.Send(new SetGameNewsSourcesCommand(Guild, Mira, false, 730, GameNewsSources.All))).Error.Description);
        Assert.Equal(GameNewsSources.All, (await sender.Send(new SetGameNewsSourcesCommand(Guild, Kai, false, 730, GameNewsSources.All))).Value.Sources);
        Assert.Equal(["press-1"], await fixture.Store.DeliveredAsync(created, default));

        Assert.True((await sender.Send(new DeleteGameNewsCommand(Guild, Mira, false, 730))).IsFailure);
        Assert.True((await sender.Send(new DeleteGameNewsCommand(Guild, Mira, true, 730))).IsSuccess);
        Assert.Equal((500UL, 600UL), fixture.Gateway.Deleted.Single());
        Assert.Empty((await sender.Send(new GetGameNewsQuery(Guild))).Value);

        // A Discord refusal comes back as the person-facing message, and nothing is saved.
        fixture.Gateway.Refuse = "HappyMix needs View Channel, Send Messages, Embed Links, and Mention Everyone in the news channel.";
        Assert.Equal(fixture.Gateway.Refuse, (await sender.Send(new CreateGameNewsCommand(Guild, Kai, 570, "Dota 2"))).Error.Description);
        Assert.Empty((await sender.Send(new GetGameNewsQuery(Guild))).Value);
    }

    [Fact]
    public async Task OpenGroupsAreFoundByNameAndSuggestedForTheRightCommands()
    {
        using var fixture = await Fixture.CreateAsync();
        var groups = fixture.Storage.GetRequiredService<IGuildGroupStore>();
        await groups.UpdateAsync(Guild, "Squad", _ => new GuildGroup("Squad", Kai, [Kai, Mira], [], Open: true));
        await groups.UpdateAsync(Guild, "Secret", _ => new GuildGroup("Secret", Kai, [Kai], []));
        var sender = fixture.Sender;
        var stranger = new GroupActor(Guild, Stranger, false);

        Assert.True((await sender.Send(new FindGroupQuery(stranger, "squad"))).IsSuccess);
        Assert.True((await sender.Send(new FindGroupQuery(stranger, "Secret"))).IsFailure);
        Assert.Equal(["Squad"], (await sender.Send(new SuggestGroupNamesQuery(stranger, "request-join", ""))).Value);
        Assert.Empty((await sender.Send(new SuggestGroupNamesQuery(stranger, "withdraw-request", ""))).Value);

        var squad = (await sender.Send(new FindGroupQuery(stranger, "Squad"))).Value;
        Assert.True((await sender.Send(new RequestToJoinGroupCommand(stranger, squad))).IsSuccess);
        Assert.Empty((await sender.Send(new SuggestGroupNamesQuery(stranger, "request-join", ""))).Value);
        Assert.Equal(["Squad"], (await sender.Send(new SuggestGroupNamesQuery(stranger, "withdraw-request", ""))).Value);
        Assert.Equal(["Squad"], (await sender.Send(new SuggestGroupNamesQuery(new GroupActor(Guild, Mira, false), "watch-show", ""))).Value);
        Assert.Equal(["Secret", "Squad"], (await sender.Send(new SuggestGroupNamesQuery(new GroupActor(Guild, Kai, false), "suggest-game", ""))).Value.Order());
    }

    [Fact]
    public void OrganizerMessagesCarryAnswerButtonsAndSearchPicksAreRead()
    {
        var group = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var suggestion = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var request = DiscordGroupNotifier.Buttons(new GroupNotice(GroupNoticeKind.JoinRequested, Guild, group, "Squad", Kai, Stranger), "en")!;
        var ids = request.Components.OfType<Discord.ActionRowComponent>().SelectMany(row => row.Components).OfType<Discord.ButtonComponent>().Select(button => button.CustomId).ToArray();
        Assert.Equal([$"group-request:1:{group:N}:99:1", $"group-request:1:{group:N}:99:0"], ids);
        var suggested = DiscordGroupNotifier.Buttons(new GroupNotice(GroupNoticeKind.GameSuggested, Guild, group, "Squad", Kai, Mira, "Lethal Company", suggestion), "uk")!;
        Assert.Contains(suggested.Components.OfType<Discord.ActionRowComponent>().SelectMany(row => row.Components).OfType<Discord.ButtonComponent>(), button => button.CustomId == $"group-suggestion:1:{group:N}:{suggestion:N}:1");
        Assert.Null(DiscordGroupNotifier.Buttons(new GroupNotice(GroupNoticeKind.JoinApproved, Guild, group, "Squad", Stranger, Kai), "en"));

        Assert.Equal(("steam", "730", "Counter-Strike 2"), PlayerGameAutocomplete.ParseSearchValue("add|steam|730|Counter-Strike 2"));
        Assert.Null(PlayerGameAutocomplete.ParseSearchValue("steam:730"));
        Assert.Null(PlayerGameAutocomplete.ParseSearchValue("add|epic|1|Fortnite"));
    }

    [Fact]
    public async Task DiscordDiscoversTheNewCommands()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client.Rest);
        using var fixture = await Fixture.CreateAsync();
        var games = await interactions.AddModuleAsync<PlayerGamesModule>(fixture.Application);
        Assert.Equal(["import", "list", "mark", "remove", "steam"], games.SlashCommands.Select(command => command.Name).Order());
        var watch = await interactions.AddModuleAsync<WatchModule>(fixture.Application);
        Assert.Equal(["finish", "next", "show"], watch.SlashCommands.Select(command => command.Name).Order());
        var news = await interactions.AddModuleAsync<GameNewsModule>(fixture.Application);
        Assert.Contains(news.SlashCommands, command => command.Name == "sources" && command.Parameters.Any(parameter => parameter.Name == "patch-notes"));
        var events = await interactions.AddModuleAsync<EventModule>(fixture.Application);
        Assert.Contains(events.SlashCommands, command => command.Name == "transfer");
        Assert.Contains(events.SlashCommands, command => command.Name == "confirm" && command.Parameters.Any(parameter => parameter.Name == "all-dates"));
        var availability = await interactions.AddModuleAsync<AvailabilityModule>(fixture.Application);
        Assert.Contains(availability.SlashCommands, command => command.Name == "who");
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        public ServiceProvider Application { get; private set; } = null!;
        public FakeGateway Gateway { get; } = new();
        public ISender Sender => Application.GetRequiredService<ISender>();
        public ServiceProvider Storage => database.Services;
        public IGameNewsStore Store => database.Services.GetRequiredService<IGameNewsStore>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.Application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton(storage.GetRequiredService<IPlayerGameStore>())
                .AddSingleton(storage.GetRequiredService<IGameNewsStore>())
                .AddSingleton(storage.GetRequiredService<IClock>())
                .AddSingleton<IMediaCatalog>(new HappyMix.Application.Tests.Polls.FakeCatalog())
                .AddSingleton<IGameNewsGateway>(fixture.Gateway)
                .AddSingleton<ISteamDirectory>(new NoSteam())
                .AddSingleton<IGroupRoleGateway>(new NoRoles())
                .AddSingleton(Microsoft.Extensions.Options.Options.Create(new HappyMix.Infrastructure.PandaScore.PandaScoreOptions()))
                .AddSingleton(Microsoft.Extensions.Options.Options.Create(new CommunityDefaults()))
                .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        public void Dispose()
        {
            Application?.Dispose();
            database?.Dispose();
        }
    }

    private sealed class FakeGateway : IGameNewsGateway
    {
        public string? Refuse { get; set; }
        public List<(ulong User, ulong Role, bool Add)> Roles { get; } = [];
        public List<(ulong Channel, ulong Role)> Deleted { get; } = [];

        public Task<(ulong ChannelId, ulong RoleId)> CreateAsync(ulong guildId, ulong ownerId, uint appId, string name, CancellationToken ct) =>
            Refuse is { } message ? throw new GameNewsException(message) : Task.FromResult((500UL, 600UL));

        public Task DeleteAsync(ulong guildId, ulong channelId, ulong roleId, CancellationToken ct)
        {
            Deleted.Add((channelId, roleId));
            return Task.CompletedTask;
        }

        public Task SetRoleAsync(ulong guildId, ulong userId, ulong roleId, bool add, CancellationToken ct)
        {
            Roles.Add((userId, roleId, add));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GameNewsPost>> ReadPostsAsync(uint appId, long since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GameNewsPost>>([new("patch-1", GameNewsSources.PatchNotes), new("press-1", GameNewsSources.Press)]);
    }

    private sealed class NoSteam : ISteamDirectory
    {
        public bool CanImport => false;
        public Task<string?> ResolveAsync(string input, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<SteamLibrary> GetOwnedGamesAsync(string steamId, CancellationToken cancellationToken) =>
            Task.FromResult(new SteamLibrary(SteamLibraryStatus.NotConfigured, []));
        public Task<string?> VerifySignInAsync(IReadOnlyDictionary<string, string> response, string returnTo, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<string, string>> GetPlayingAsync(IReadOnlyCollection<string> steamIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class NoRoles : IGroupRoleGateway
    {
        public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) => Task.FromResult<IReadOnlyCollection<ulong>>([]);
        public Task<ulong> CreateAsync(ulong guildId, string name) => Task.FromResult(0UL);
        public Task DeleteAsync(ulong guildId, ulong roleId) => Task.CompletedTask;
        public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta) => Task.CompletedTask;
    }
}
