using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Application.Tests.Persistence;
using HappyMix.Infrastructure;
using HappyMix.Infrastructure.Persistence;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Groups;

public sealed class GroupWorkspaceTests
{
    private const ulong Guild = 1;
    private const ulong OtherGuild = 2;
    private const ulong Organizer = 10;
    private const ulong Member = 20;
    private const ulong OtherOrganizer = 30;
    private const ulong Stranger = 99;
    private const ulong Moderator = 77;

    [Fact]
    public async Task DirectoryShowsOwnGroupsAndInvitationsAndModeratorsSeeTheWholeServer()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var sender = fixture.Sender;

        var mine = (await sender.Send(new GetGroupDirectoryQuery(Actor(Member), GroupDirectoryFilter.Mine, null, 1))).Value;
        Assert.Equal(["Friday"], mine.Items.Select(group => group.Name));
        Assert.Equal((1, 1, 2), (mine.MineCount, mine.InvitedCount, mine.AllCount));

        var invited = (await sender.Send(new GetGroupDirectoryQuery(Actor(Member), GroupDirectoryFilter.Invited, null, 1))).Value;
        var pending = Assert.Single(invited.Items);
        Assert.Equal(("Weekend", GroupRelation.Invited, false), (pending.Name, pending.Relation, pending.CanManage));
        Assert.Empty(pending.Games);

        var all = (await sender.Send(new GetGroupDirectoryQuery(Actor(Member), GroupDirectoryFilter.All, null, 1))).Value;
        Assert.DoesNotContain(all.Items, group => group.Name == "Private");

        Assert.Empty((await sender.Send(new GetGroupDirectoryQuery(Actor(Stranger), GroupDirectoryFilter.All, null, 1))).Value.Items);

        var moderator = (await sender.Send(new GetGroupDirectoryQuery(Actor(Moderator, moderate: true), GroupDirectoryFilter.All, null, 1))).Value;
        Assert.Equal(3, moderator.Total);
        Assert.All(moderator.Items, group => Assert.True(group.CanManage));
        // Each server's directory holds only its own groups.
        var otherServer = (await sender.Send(new GetGroupDirectoryQuery(new GroupActor(OtherGuild, Moderator, true),
            GroupDirectoryFilter.All, null, 1))).Value;
        Assert.Equal(["Elsewhere"], otherServer.Items.Select(group => group.Name));
    }

    [Fact]
    public async Task DirectorySearchesNamesDescriptionsAndGamesAndPaginates()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var moderator = Actor(Moderator, true);

        var byDescription = (await fixture.Sender.Send(new GetGroupDirectoryQuery(moderator, GroupDirectoryFilter.All, "co-op", 1))).Value;
        Assert.Equal(["Friday"], byDescription.Items.Select(group => group.Name));
        var byGame = (await fixture.Sender.Send(new GetGroupDirectoryQuery(moderator, GroupDirectoryFilter.All, "dota", 1))).Value;
        Assert.Equal(["Friday"], byGame.Items.Select(group => group.Name));

        var first = (await fixture.Sender.Send(new GetGroupDirectoryQuery(moderator, GroupDirectoryFilter.All, null, 1, PageSize: 2))).Value;
        Assert.Equal((2, 2, 3), (first.Items.Length, first.PageCount, first.Total));
        var beyond = (await fixture.Sender.Send(new GetGroupDirectoryQuery(moderator, GroupDirectoryFilter.All, null, 9, PageSize: 2))).Value;
        Assert.Equal(2, beyond.Page);
        Assert.Single(beyond.Items);
    }

    [Fact]
    public async Task RosterIsLimitedToMembersManagersAndModeratorsAndScopedToTheServer()
    {
        using var fixture = await Fixture.CreateAsync();
        var (friday, weekend, _) = await fixture.SeedAsync();
        var sender = fixture.Sender;

        var strangerView = await sender.Send(new GetGroupDetailQuery(Actor(Stranger), friday));
        Assert.True(strangerView.IsFailure);
        Assert.Equal("Group.NotFound", strangerView.Error.Code);
        // A valid ID from another server never resolves, even for that server's moderator.
        Assert.True((await sender.Send(new GetGroupDetailQuery(new GroupActor(OtherGuild, Moderator, true), friday))).IsFailure);

        var invitee = (await sender.Send(new GetGroupDetailQuery(Actor(Member), weekend))).Value;
        Assert.False(invitee.CanViewRoster);
        Assert.Empty(invitee.Members);
        Assert.Empty(invitee.Games);
        Assert.Empty(invitee.Invites);
        Assert.Equal(1, invitee.Summary.MemberCount);

        var member = (await sender.Send(new GetGroupDetailQuery(Actor(Member), friday))).Value;
        Assert.True(member.CanViewRoster);
        Assert.Equal([Organizer, Member], member.Members);
        Assert.Equal(["Dota 2"], member.Games.Select(game => game.Name));
        Assert.Empty(member.Invites);
        Assert.True(member.CanLeave);

        var organizer = (await sender.Send(new GetGroupDetailQuery(Actor(Organizer), friday))).Value;
        Assert.Equal([55UL], organizer.Invites);
        Assert.False(organizer.CanLeave);

        var moderator = (await sender.Send(new GetGroupDetailQuery(Actor(Moderator, true), weekend))).Value;
        Assert.True(moderator.Summary.CanManage);
        Assert.Equal([Member], moderator.Invites);
    }

    [Fact]
    public async Task OnlyOrganizersAndModeratorsCanManageAndHiddenGroupsRevealNothing()
    {
        using var fixture = await Fixture.CreateAsync();
        var (friday, _, _) = await fixture.SeedAsync();
        var sender = fixture.Sender;
        var member = Actor(Member);

        Assert.True((await sender.Send(new AddGroupGameCommand(member, friday, "Halo", null))).IsFailure);
        Assert.True((await sender.Send(new EditGroupDescriptionCommand(member, "Friday", "Hijacked"))).IsFailure);
        Assert.True((await sender.Send(new InviteGroupMemberCommand(member, "Friday", 66, Guid.NewGuid()))).IsFailure);
        Assert.True((await sender.Send(new RemoveGroupMemberCommand(member, "Friday", Organizer))).IsFailure);
        Assert.True((await sender.Send(new DeleteGroupCommand(member, "Friday"))).IsFailure);
        Assert.True((await sender.Send(new RepairGroupRolesCommand(member, null))).IsFailure);

        // A modified form that targets a group the user can't see gets the same answer as a missing group.
        var hidden = await sender.Send(new AddGroupGameCommand(Actor(Stranger), friday, "Halo", null));
        Assert.Equal("That group doesn't exist or you don't have access to it.", hidden.Error.Description);

        Assert.True((await sender.Send(new AddGroupGameCommand(Actor(Organizer), friday, "Halo", null))).IsSuccess);
        Assert.True((await sender.Send(new AddGroupGameCommand(Actor(Moderator, true), friday, "Portal 2", "620"))).IsSuccess);
        Assert.True((await sender.Send(new EditGroupDescriptionCommand(Actor(Moderator, true), "Friday", "Moderated"))).IsSuccess);
        Assert.True((await sender.Send(new RepairGroupRolesCommand(Actor(Moderator, true), Member))).IsSuccess);

        var group = (await sender.Send(new GetGroupDetailQuery(Actor(Organizer), friday))).Value;
        Assert.Equal(["Dota 2", "Halo", "Portal 2"], group.Games.Select(game => game.Name));
        Assert.Equal("Moderated", group.Summary.Description);
    }

    [Fact]
    public async Task InvitationsCanOnlyBeResolvedWhilePendingAndOrganizersMustCloseToLeave()
    {
        using var fixture = await Fixture.CreateAsync();
        var (friday, _, _) = await fixture.SeedAsync();
        var sender = fixture.Sender;
        var organizer = Actor(Organizer);

        Guid token = Guid.NewGuid();
        Assert.True((await sender.Send(new InviteGroupMemberCommand(organizer, "Friday", 60, token))).IsSuccess);
        Assert.True((await sender.Send(new RemoveGroupMemberCommand(organizer, "Friday", 60))).IsSuccess);
        Assert.True((await sender.Send(new ResolveGroupInviteCommand(Actor(60), "Friday", null, true))).IsFailure);
        Assert.True((await sender.Send(new ResolveGroupInviteCommand(Actor(60), null, token, true))).IsFailure);

        // Someone else can't accept an invitation that isn't theirs.
        Assert.True((await sender.Send(new ResolveGroupInviteCommand(Actor(Stranger), "Friday", null, true))).IsFailure);
        Assert.True((await sender.Send(new ResolveGroupInviteCommand(Actor(55), "Friday", null, true))).IsSuccess);
        var joined = (await sender.Send(new GetGroupDetailQuery(Actor(55), friday))).Value;
        Assert.Contains(55UL, joined.Members);
        Assert.Equal(GroupRelation.Member, joined.Summary.Relation);

        Assert.True((await sender.Send(new LeaveGroupCommand(organizer, "Friday"))).IsFailure);
        Assert.True((await sender.Send(new LeaveGroupCommand(Actor(Member), "Friday"))).IsSuccess);
        Assert.True((await sender.Send(new GetGroupDetailQuery(Actor(Member), friday))).IsFailure);
    }

    [Fact]
    public async Task SavedChangesReportFailedRoleSynchronization()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Gateway.Fail = true;

        var result = await fixture.Sender.Send(new ResolveGroupInviteCommand(Actor(55), "Friday", null, true));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.RoleSyncFailed);
        var group = Assert.Single(await fixture.Store.ListAsync(Guild), item => item.Name == "Friday");
        Assert.Contains(55UL, group.Members);
    }

    [Theory]
    [InlineData("730", "730")]
    [InlineData(" 0730 ", "730")]
    [InlineData("https://store.steampowered.com/app/730/CounterStrike_2/", "730")]
    [InlineData("store.steampowered.com/app/570", "570")]
    [InlineData("http://www.steamcommunity.com/app/730/", "730")]
    [InlineData("", null)]
    public void SteamInputNormalizesToTheAppId(string input, string? expected)
    {
        Assert.True(SteamApps.TryNormalize(input, out string? appId));
        Assert.Equal(expected, appId);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("99999999999")]
    [InlineData("https://example.com/app/730")]
    [InlineData("https://store.steampowered.com/sub/730")]
    [InlineData("https://user@store.steampowered.com/app/730")]
    public void SteamInputRejectsAnythingElse(string input) => Assert.False(SteamApps.TryNormalize(input, out _));

    [Fact]
    public async Task GamesDeduplicateSteamIdsPerGroupAndCanBeLinkedLaterAndRemoved()
    {
        using var fixture = await Fixture.CreateAsync();
        var (friday, weekend, _) = await fixture.SeedAsync();
        var sender = fixture.Sender;
        var organizer = Actor(Organizer);

        Assert.True((await sender.Send(new AddGroupGameCommand(organizer, friday, "CS2", "730"))).IsSuccess);
        var duplicate = await sender.Send(new AddGroupGameCommand(organizer, friday, "Counter-Strike",
            "https://store.steampowered.com/app/730/"));
        Assert.Equal("This group already has that Steam game.", duplicate.Error.Description);
        Assert.True((await sender.Send(new AddGroupGameCommand(organizer, friday, "Tabletop night", null))).IsSuccess);
        Assert.True((await sender.Send(new AddGroupGameCommand(organizer, friday, "LAN party", " "))).IsSuccess);
        Assert.True((await sender.Send(new AddGroupGameCommand(Actor(OtherOrganizer), weekend, "CS2", "730"))).IsSuccess);
        Assert.True((await sender.Send(new AddGroupGameCommand(organizer, friday, "Bad", "steam:730"))).IsFailure);

        var games = (await sender.Send(new GetGroupDetailQuery(organizer, friday))).Value.Games;
        var tabletop = games.Single(game => game.Name == "Tabletop night");
        Assert.Null(tabletop.SteamAppId);
        Assert.Equal(GroupGame.SteamProvider, games.Single(game => game.Name == "CS2").Provider);

        Assert.True((await sender.Send(new UpdateGroupGameCommand(organizer, friday, tabletop.Id, "Tabletop Simulator",
            "https://store.steampowered.com/app/286160/"))).IsSuccess);
        var linked = (await sender.Send(new GetGroupDetailQuery(organizer, friday))).Value.Games.Single(game => game.Id == tabletop.Id);
        Assert.Equal(("Tabletop Simulator", "286160"), (linked.Name, linked.SteamAppId));
        var lan = games.Single(game => game.Name == "LAN party");
        Assert.True((await sender.Send(new UpdateGroupGameCommand(organizer, friday, lan.Id, "LAN party", "286160"))).IsFailure);

        // A game ID from one group can't be removed through another group, even by a moderator.
        Assert.True((await sender.Send(new RemoveGroupGameCommand(Actor(Moderator, true), weekend, tabletop.Id))).IsFailure);
        Assert.True((await sender.Send(new RemoveGroupGameCommand(organizer, friday, tabletop.Id))).IsSuccess);
        Assert.True((await sender.Send(new RemoveGroupGameCommand(organizer, friday, tabletop.Id))).IsFailure);
        Assert.DoesNotContain((await sender.Send(new GetGroupDetailQuery(organizer, friday))).Value.Games,
            game => game.Id == tabletop.Id);
    }

    [Fact]
    public async Task CreatingAGroupAttachesGamesAndRejectsDuplicatesBeforeSaving()
    {
        using var fixture = await Fixture.CreateAsync();
        var sender = fixture.Sender;

        var rejected = await sender.Send(new CreateGroupCommand(Actor(Organizer), "Raid night", null, [],
            [new("CS2", "730"), new("Counter-Strike 2", "store.steampowered.com/app/730")]));
        Assert.True(rejected.IsFailure);
        Assert.Empty(await fixture.Store.ListAsync(Guild));

        var created = await sender.Send(new CreateGroupCommand(Actor(Organizer), "Raid night", "Weekly raids", [],
            [new("CS2", "730"), new("Board games", null)]));
        Assert.True(created.IsSuccess);
        Guid id = Assert.NotNull(created.Value.GroupId);
        var detail = (await sender.Send(new GetGroupDetailQuery(Actor(Organizer), id))).Value;
        Assert.Equal(GroupRelation.Organizer, detail.Summary.Relation);
        Assert.Equal(["Board games", "CS2"], detail.Games.Select(game => game.Name));
    }

    [Fact]
    public async Task CreationFollowsTheOrganizerRoleRule()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.UpdateAsync(Guild, "UTC", saved => saved with
        {
            Preferences = GuildEsportsPreferences.Default with { OrganizerRoleIds = [500] }
        });

        Assert.False((await fixture.Sender.Send(new CanCreateGroupQuery(Actor(Member), [400]))).Value);
        Assert.True((await fixture.Sender.Send(new CanCreateGroupQuery(Actor(Member), [500]))).Value);
        Assert.True((await fixture.Sender.Send(new CanCreateGroupQuery(Actor(Moderator, true), []))).Value);
        Assert.True((await fixture.Sender.Send(new CreateGroupCommand(Actor(Member), "Nope", null, [400]))).IsFailure);
    }

    [Fact]
    public async Task ClosingAGroupUsesTheRoleDeletionFlowAndRemovesItsGames()
    {
        using var fixture = await Fixture.CreateAsync();
        var (friday, _, _) = await fixture.SeedAsync();

        Assert.True((await fixture.Sender.Send(new DeleteGroupCommand(Actor(Organizer), "Friday"))).IsSuccess);

        Assert.Contains(321UL, fixture.Gateway.Deleted);
        Assert.DoesNotContain(await fixture.Store.ListAsync(Guild), group => group.Id == friday);
        Assert.DoesNotContain(await fixture.Store.ListGamesAsync(Guild), game => game.GroupId == friday);
        Assert.True((await fixture.Sender.Send(new GetGroupDetailQuery(Actor(Moderator, true), friday))).IsFailure);
    }

    [Fact]
    public async Task UpgradingAnExistingDatabaseBackfillsDurableIdsWithoutChangingGroups()
    {
        string directory = Path.Combine(Path.GetTempPath(), "HappyMix-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "groups.db");
        try
        {
            using (var legacy = BuildProvider(path))
            {
                await using var context = await legacy.GetRequiredService<IDbContextFactory<HappyMixDbContext>>().CreateDbContextAsync();
                Directory.CreateDirectory(directory);
                await context.GetService<IMigrator>().MigrateAsync("20261114000100_AddWeeklyAvailability");
                Guid token = Guid.Parse("5a0a1f2e-0000-4000-8000-000000000001");
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO GuildGroups (GuildId, NameKey, Data) VALUES ('1', 'FRIDAY', {0}), ('1', 'WEEKEND', {1})",
                    $$"""{"Name":"Friday","OrganizerId":10,"Members":[10,20],"Invites":[30],"InviteTokens":{"30":"{{token}}"},"Description":"Co-op","RoleId":321}""",
                    """{"Name":"Weekend","OrganizerId":40,"Members":[40],"Invites":[],"InviteTokens":null,"Description":null,"RoleId":null}""");
            }

            using var upgraded = BuildProvider(path);
            await upgraded.InitializeDatabaseAsync();
            var store = upgraded.GetRequiredService<IGuildGroupStore>();
            var groups = await store.ListAsync(1);

            Assert.Equal(2, groups.Count);
            Assert.All(groups, group => Assert.NotEqual(Guid.Empty, group.Id));
            Assert.Equal(2, groups.Select(group => group.Id).Distinct().Count());
            var friday = groups.Single(group => group.Name == "Friday");
            Assert.Equal([10UL, 20UL], friday.Members);
            Assert.Equal([30UL], friday.Invites);
            Assert.Equal("Co-op", friday.Description);
            Assert.Equal(321UL, friday.RoleId);
            Assert.Equal(Guid.Parse("5a0a1f2e-0000-4000-8000-000000000001"), friday.InviteTokens![30]);

            // IDs are durable: they don't change on later reads or updates.
            await store.UpdateAsync(1, "Friday", group => group! with { Description = "Updated" });
            Assert.Equal(friday.Id, (await store.ListAsync(1)).Single(group => group.Name == "Friday").Id);
            Assert.True(await store.SaveGameAsync(1, new GroupGame(Guid.NewGuid(), friday.Id, "CS2", GroupGame.SteamProvider, "730")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static GroupActor Actor(ulong user, bool moderate = false) => new(Guild, user, moderate);

    private static ServiceProvider BuildProvider(string path)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabasePath"] = path,
            ["Discord:AnnouncementChannelId"] = "123"
        }).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;

        public RecordingGateway Gateway { get; } = new();
        public IGuildGroupStore Store => database.Services.GetRequiredService<IGuildGroupStore>();
        public IGuildSettingsStore Settings => database.Services.GetRequiredService<IGuildSettingsStore>();
        public ISender Sender => application.GetRequiredService<ISender>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(fixture.Store).AddSingleton(fixture.Settings)
                .AddSingleton<IGroupRoleGateway>(fixture.Gateway)
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        /// <summary>Friday: organizer 10, member 20, invite 55. Weekend: organizer 30, invite 20. Private: organizer 40.</summary>
        public async Task<(Guid Friday, Guid Weekend, Guid Private)> SeedAsync()
        {
            await Store.UpdateAsync(Guild, "Friday", _ => new GuildGroup("Friday", Organizer, [Organizer, Member], [55],
                new() { [55] = Guid.NewGuid() }, "Friday co-op nights", RoleId: 321));
            await Store.UpdateAsync(Guild, "Weekend", _ => new GuildGroup("Weekend", OtherOrganizer, [OtherOrganizer], [Member],
                new() { [Member] = Guid.NewGuid() }));
            await Store.UpdateAsync(Guild, "Private", _ => new GuildGroup("Private", 40, [40], []));
            await Store.UpdateAsync(OtherGuild, "Elsewhere", _ => new GuildGroup("Elsewhere", Organizer, [Organizer], []));
            var groups = await Store.ListAsync(Guild);
            Guid Id(string name) => groups.Single(group => group.Name == name).Id;
            await Store.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), Id("Friday"), "Dota 2", GroupGame.SteamProvider, "570"));
            await Store.SaveGameAsync(Guild, new GroupGame(Guid.NewGuid(), Id("Weekend"), "Rocket League"));
            return (Id("Friday"), Id("Weekend"), Id("Private"));
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }

    private sealed class RecordingGateway : IGroupRoleGateway
    {
        public bool Fail { get; set; }
        public List<ulong> Deleted { get; } = [];

        public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) =>
            Task.FromResult<IReadOnlyCollection<ulong>>([321]);

        public Task<ulong> CreateAsync(ulong guildId, string name) => Task.FromResult(900UL);

        public Task DeleteAsync(ulong guildId, ulong roleId)
        {
            Deleted.Add(roleId);
            return Task.CompletedTask;
        }

        public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta) =>
            Fail ? throw new GroupRoleException("Discord rejected the role operation (403).", new InvalidOperationException())
                : Task.CompletedTask;
    }
}
