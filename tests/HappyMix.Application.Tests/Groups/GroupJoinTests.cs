using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Application.Tests.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Groups;

/// <summary>Open groups, join requests and game suggestions.</summary>
public sealed class GroupJoinTests
{
    private const ulong Guild = 1, Organizer = 10, Member = 20, Stranger = 99, Moderator = 77;
    private static GroupActor Actor(ulong user, bool moderate = false) => new(Guild, user, moderate);

    [Fact]
    public async Task OpenGroupsAreListedForEveryoneAndRequestsNeedTheOrganizer()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync();
        var sender = fixture.Sender;

        // Closed by default: strangers don't see it and can't ask.
        Assert.Empty((await sender.Send(new GetGroupDirectoryQuery(Actor(Stranger), GroupDirectoryFilter.All, null, 1))).Value.Items);
        Assert.True((await sender.Send(new RequestToJoinGroupCommand(Actor(Stranger), group))).IsFailure);
        Assert.True((await sender.Send(new SetGroupOpenCommand(Actor(Member), group, true))).IsFailure);

        Assert.True((await sender.Send(new SetGroupOpenCommand(Actor(Organizer), group, true))).IsSuccess);
        var open = (await sender.Send(new GetGroupDirectoryQuery(Actor(Stranger), GroupDirectoryFilter.Open, null, 1))).Value;
        var listed = Assert.Single(open.Items);
        Assert.Equal((true, GroupRelation.None, 1), (listed.Open, listed.Relation, open.OpenCount));
        var outside = (await sender.Send(new GetGroupDetailQuery(Actor(Stranger), group))).Value;
        Assert.False(outside.CanViewRoster);
        Assert.Empty(outside.Members);

        Assert.True((await sender.Send(new RequestToJoinGroupCommand(Actor(Stranger), group))).IsSuccess);
        Assert.Equal(GroupRelation.Requested, (await sender.Send(new GetGroupDetailQuery(Actor(Stranger), group))).Value.Summary.Relation);
        var notice = Assert.Single(fixture.Notices);
        Assert.Equal((GroupNoticeKind.JoinRequested, Organizer, Stranger), (notice.Kind, notice.RecipientId, notice.ActorId));
        var requests = (await sender.Send(new GetGroupDetailQuery(Actor(Organizer), group))).Value.JoinRequests;
        Assert.Equal(new[] { Stranger }, requests);
        Assert.Empty((await sender.Send(new GetGroupDetailQuery(Actor(Member), group))).Value.JoinRequests);

        Assert.True((await sender.Send(new ResolveJoinRequestCommand(Actor(Member), group, Stranger, true))).IsFailure);
        Assert.True((await sender.Send(new ResolveJoinRequestCommand(Actor(Organizer), group, Stranger, true))).IsSuccess);
        var joined = (await sender.Send(new GetGroupDetailQuery(Actor(Stranger), group))).Value;
        Assert.Equal(GroupRelation.Member, joined.Summary.Relation);
        Assert.Contains(Stranger, joined.Members);
        Assert.Equal(GroupNoticeKind.JoinApproved, fixture.Notices[^1].Kind);
        Assert.True((await sender.Send(new ResolveJoinRequestCommand(Actor(Organizer), group, Stranger, true))).IsFailure);
    }

    [Fact]
    public async Task RequestsCanBeWithdrawnOrDeclinedAndClosingDropsThem()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync();
        var sender = fixture.Sender;
        await sender.Send(new SetGroupOpenCommand(Actor(Moderator, moderate: true), group, true));

        await sender.Send(new RequestToJoinGroupCommand(Actor(Stranger), group));
        await sender.Send(new CancelJoinRequestCommand(Actor(Stranger), group));
        Assert.Empty((await sender.Send(new GetGroupDetailQuery(Actor(Organizer), group))).Value.JoinRequests);

        await sender.Send(new RequestToJoinGroupCommand(Actor(Stranger), group));
        await sender.Send(new ResolveJoinRequestCommand(Actor(Organizer), group, Stranger, false));
        Assert.Equal(GroupNoticeKind.JoinDeclined, fixture.Notices[^1].Kind);
        Assert.Equal(GroupRelation.None, (await sender.Send(new GetGroupDetailQuery(Actor(Stranger), group))).Value.Summary.Relation);

        await sender.Send(new RequestToJoinGroupCommand(Actor(Stranger), group));
        await sender.Send(new SetGroupOpenCommand(Actor(Organizer), group, false));
        Assert.Empty((await sender.Send(new GetGroupDetailQuery(Actor(Organizer), group))).Value.JoinRequests);
        Assert.True((await sender.Send(new GetGroupDetailQuery(Actor(Stranger), group))).IsFailure);
    }

    [Fact]
    public async Task MembersSuggestGamesFromAnyPlatformAndTheOrganizerAddsThem()
    {
        using var fixture = await Fixture.CreateAsync();
        var group = await fixture.GroupAsync();
        var sender = fixture.Sender;

        Assert.True((await sender.Send(new SuggestGroupGameCommand(Actor(Stranger), group, "Fortnite", null))).IsFailure);
        Assert.True((await sender.Send(new SuggestGroupGameCommand(Actor(Member), group, "League of Legends", null, "igdb", "115"))).IsSuccess);
        Assert.True((await sender.Send(new SuggestGroupGameCommand(Actor(Member), group, "league of legends", null))).IsFailure);
        Assert.True((await sender.Send(new SuggestGroupGameCommand(Actor(Member), group, "Dota 2", null))).IsFailure);
        Assert.Equal(GroupNoticeKind.GameSuggested, fixture.Notices[^1].Kind);

        var suggestion = Assert.Single((await sender.Send(new GetGroupDetailQuery(Actor(Organizer), group))).Value.Suggestions);
        Assert.Equal(("League of Legends", "igdb", "115", Member), (suggestion.Name, suggestion.Provider, suggestion.ExternalId, suggestion.SuggestedBy));
        Assert.True((await sender.Send(new ResolveGameSuggestionCommand(Actor(Member), group, suggestion.Id, true))).IsFailure);
        Assert.True((await sender.Send(new ResolveGameSuggestionCommand(Actor(Organizer), group, suggestion.Id, true))).IsSuccess);

        var detail = (await sender.Send(new GetGroupDetailQuery(Actor(Member), group))).Value;
        Assert.Empty(detail.Suggestions);
        var game = Assert.Single(detail.Games, item => item.Name == "League of Legends");
        Assert.Equal(("igdb", "115", (string?)null), (game.Provider, game.ExternalId, game.SteamAppId));
        Assert.Equal(GroupNoticeKind.SuggestionAccepted, fixture.Notices[^1].Kind);

        // Picking a search result that is also on Steam keeps the Steam id; any other platform keeps its catalog id.
        Assert.True((await sender.Send(new AddGroupGameCommand(Actor(Organizer), group, "Deep Rock Galactic", "548430", "steam", "548430"))).IsSuccess);
        Assert.True((await sender.Send(new AddGroupGameCommand(Actor(Organizer), group, "Fortnite", null, "igdb", "1905"))).IsSuccess);
        Assert.True((await sender.Send(new AddGroupGameCommand(Actor(Organizer), group, "Fortnite", null, "igdb", "1905"))).IsFailure);
        var games = (await sender.Send(new GetGroupDetailQuery(Actor(Member), group))).Value.Games;
        Assert.Equal("548430", games.Single(item => item.Name == "Deep Rock Galactic").SteamAppId);
        Assert.Equal("igdb", games.Single(item => item.Name == "Fortnite").Provider);
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;
        public List<GroupNotice> Notices { get; } = [];
        public ISender Sender => application.GetRequiredService<ISender>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            var storage = fixture.database.Services;
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(storage.GetRequiredService<IGuildGroupStore>())
                .AddSingleton(storage.GetRequiredService<IGuildSettingsStore>())
                .AddSingleton(storage.GetRequiredService<IAccountLinkStore>())
                .AddSingleton<IGroupRoleGateway>(new NoRoles())
                .AddSingleton<IGroupNotifier>(new RecordingNotifier(fixture.Notices))
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        /// <summary>Squad: organizer 10 and member 20, with Dota 2 attached.</summary>
        public async Task<Guid> GroupAsync()
        {
            var store = database.Services.GetRequiredService<IGuildGroupStore>();
            await store.UpdateAsync(Guild, "Squad", _ => new GuildGroup("Squad", Organizer, [Organizer, Member], []));
            var id = (await store.ListAsync(Guild)).Single().Id;
            await store.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), id, "Dota 2", GroupGame.SteamProvider, "570"));
            return id;
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }

    private sealed class RecordingNotifier(List<GroupNotice> notices) : IGroupNotifier
    {
        public Task NotifyAsync(GroupNotice notice, CancellationToken cancellationToken)
        {
            notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    private sealed class NoRoles : IGroupRoleGateway
    {
        public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) => Task.FromResult<IReadOnlyCollection<ulong>>([]);
        public Task<ulong> CreateAsync(ulong guildId, string name) => Task.FromResult(0UL);
        public Task DeleteAsync(ulong guildId, ulong roleId) => Task.CompletedTask;
        public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta) => Task.CompletedTask;
    }
}
