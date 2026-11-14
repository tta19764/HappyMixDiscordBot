using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Application.Tests.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Groups;

public sealed class AccountLinkTests
{
    private const ulong Guild = 1;
    private const ulong OtherGuild = 2;
    private const ulong Main = 10;
    private const ulong Twink = 11;
    private const ulong SecondTwink = 12;
    private const ulong Friend = 20;
    private const ulong Stranger = 99;
    private const string Kyiv = "Europe/Kyiv";

    [Fact]
    public async Task OnlyAdministratorsCanPreviewAndLinkOtherAccounts()
    {
        using var fixture = await Fixture.CreateAsync();
        var preview = new PreviewAdminAccountLinkQuery(Guild, Main, Main, Twink);
        var command = new AdminLinkAccountsCommand(Guild, Main, Main, Twink, new LinkResolutions(
            new Dictionary<DayOfWeek, ScheduleChoice>(), new Dictionary<Guid, ScheduleChoice>(), false));

        Assert.True((await fixture.Sender.Send(preview)).IsFailure);
        Assert.True((await fixture.Sender.Send(command)).IsFailure);
        Assert.True((await fixture.Sender.Send(preview with { AdministratorId = Friend })).IsSuccess);
        Assert.True((await fixture.Sender.Send(command with { AdministratorId = Friend })).IsSuccess);
        Assert.Equal(Twink, Assert.Single(await fixture.Links.ListAsync(Guild)).AlternateId);
    }

    [Fact]
    public async Task LinkingNeedsTheSecretFromThePrimaryAndApprovalFromTheAlternate()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(Main);

        Assert.True((await fixture.ApproveAsync(Twink, request.RequestId, "not-the-secret")).IsFailure);
        Assert.True((await fixture.ApproveAsync(Twink, Guid.NewGuid(), request.Token)).IsFailure);
        // The primary can't approve its own request: the second account must sign in itself.
        var self = await fixture.ApproveAsync(Main, request.RequestId, request.Token);
        Assert.Contains("alternate account", self.Error.Description);
        Assert.Empty(await fixture.Links.ListAsync(Guild));

        var linked = await fixture.ApproveAsync(Twink, request.RequestId, request.Token);
        Assert.True(linked.IsSuccess);
        Assert.Equal(Main, linked.Value.PrimaryId);
        Assert.Equal([new AccountLink(Main, Twink, Fixture.Start)], await fixture.Links.ListAsync(Guild));
    }

    [Fact]
    public async Task RequestsExpireAreSingleUseAndRetriesAreSafe()
    {
        using var fixture = await Fixture.CreateAsync();
        var expired = await fixture.RequestAsync(Main);
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(AccountLinkHandlers.Unavailable, (await fixture.ApproveAsync(Twink, expired.RequestId, expired.Token)).Error.Description);

        var request = await fixture.RequestAsync(Main);
        Assert.True((await fixture.ApproveAsync(Twink, request.RequestId, request.Token)).IsSuccess);
        // The same alternate retrying gets a harmless "already linked"; anyone else is refused.
        var retry = await fixture.ApproveAsync(Twink, request.RequestId, request.Token);
        Assert.True(retry.IsSuccess && retry.Value.AlreadyLinked);
        Assert.True((await fixture.ApproveAsync(SecondTwink, request.RequestId, request.Token)).IsFailure);

        var revoked = await fixture.RequestAsync(Main);
        Assert.True((await fixture.Sender.Send(new RevokeAccountLinkRequestCommand(Guild, Main, revoked.RequestId))).IsSuccess);
        Assert.True((await fixture.ApproveAsync(SecondTwink, revoked.RequestId, revoked.Token)).IsFailure);
        Assert.Single(await fixture.Links.ListAsync(Guild));
    }

    [Fact]
    public async Task ConcurrentApprovalsOfOneRequestLinkOnlyOnce()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(Main);

        var results = await Task.WhenAll(fixture.ApproveAsync(Twink, request.RequestId, request.Token),
            fixture.ApproveAsync(SecondTwink, request.RequestId, request.Token));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(await fixture.Links.ListAsync(Guild));
    }

    [Fact]
    public async Task LinksStayInsideOneServerAndBothAccountsMustBelongToIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = await fixture.RequestAsync(Main);
        var crossServer = await fixture.Sender.Send(new ApproveAccountLinkCommand(OtherGuild, Twink, request.RequestId, request.Token,
            LinkResolutions.None));
        Assert.True(crossServer.IsFailure);

        fixture.Members.Remove(Twink);
        Assert.Contains("members of this server", (await fixture.ApproveAsync(Twink, request.RequestId, request.Token)).Error.Description);
        fixture.Members.Add(Twink);
        Assert.True((await fixture.ApproveAsync(Twink, request.RequestId, request.Token)).IsSuccess);

        Assert.Empty(await fixture.Links.ListAsync(OtherGuild));
        await fixture.Groups.UpdateAsync(OtherGuild, "Elsewhere", _ => new GuildGroup("Elsewhere", Main, [Main], []));
        var elsewhere = (await fixture.Sender.Send(new GetGroupDirectoryQuery(new GroupActor(OtherGuild, Twink, false),
            GroupDirectoryFilter.All, null, 1))).Value;
        Assert.Empty(elsewhere.Items);
    }

    [Fact]
    public async Task FamiliesCannotChainCycleOrShareAnAlternate()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.LinkAsync(Main, Twink);

        // An alternate can't start requests or become someone else's alternate.
        Assert.True((await fixture.Sender.Send(new CreateAccountLinkRequestCommand(Guild, Twink))).IsFailure);
        var other = await fixture.RequestAsync(Friend);
        Assert.Contains("another linked family", (await fixture.ApproveAsync(Twink, other.RequestId, other.Token)).Error.Description);
        // A primary with alternates can't be attached under someone else (no chains or cycles).
        Assert.True((await fixture.ApproveAsync(Main, other.RequestId, other.Token)).IsFailure);
        // A second request for the same pair is a duplicate.
        var again = await fixture.RequestAsync(Main);
        Assert.Contains("already linked", (await fixture.ApproveAsync(Twink, again.RequestId, again.Token)).Error.Description);

        // Several alternates per primary are fine.
        await fixture.LinkAsync(Main, SecondTwink);
        Assert.Equal(2, (await fixture.Links.ListAsync(Guild)).Count);
    }

    [Fact]
    public async Task LinkingNeverLendsDiscordPermissions()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Groups.UpdateAsync(Guild, "Private", _ => new GuildGroup("Private", Friend, [Friend], []));
        await fixture.LinkAsync(Main, Twink);

        // Main may be a moderator in Discord; the twink signs in without that permission and gets none of it.
        var twink = new GroupActor(Guild, Twink, CanModerate: false);
        Assert.Empty((await fixture.Sender.Send(new GetGroupDirectoryQuery(twink, GroupDirectoryFilter.All, null, 1))).Value.Items);
        Assert.True((await fixture.Sender.Send(new RepairGroupRolesCommand(twink, null))).IsFailure);
        Assert.True((await fixture.Sender.Send(new EditGroupDescriptionCommand(twink, "Private", "Taken over"))).IsFailure);
        Assert.Single((await fixture.Sender.Send(new GetGroupDirectoryQuery(new GroupActor(Guild, Main, true),
            GroupDirectoryFilter.All, null, 1))).Value.Items);
    }

    [Fact]
    public async Task LinkedAccountsShareMembershipAndOrganizerRightsButOnlyInviteeCanAccept()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.LinkAsync(Main, Twink);
        Assert.True((await fixture.Sender.Send(new CreateGroupCommand(new GroupActor(Guild, Twink, false), "Raid", null, []))).IsSuccess);
        await fixture.Groups.UpdateAsync(Guild, "Friday", _ => new GuildGroup("Friday", Friend, [Friend], []));

        // Organizer rights follow the member: Main manages the group its twink created.
        var raid = Assert.Single(await fixture.Groups.ListAsync(Guild), group => group.Name == "Raid");
        Assert.Equal(Twink, raid.OrganizerId);
        Assert.True((await fixture.Sender.Send(new EditGroupDescriptionCommand(new GroupActor(Guild, Main, false), "Raid", "Ours"))).IsSuccess);

        // Only the addressed account can accept, and joins the family once.
        Guid token = Guid.NewGuid();
        Assert.True((await fixture.Sender.Send(new InviteGroupMemberCommand(new GroupActor(Guild, Friend, false), "Friday", Twink, token))).IsSuccess);
        var invited = (await fixture.Sender.Send(new GetGroupDirectoryQuery(new GroupActor(Guild, Main, false), GroupDirectoryFilter.Invited, null, 1))).Value;
        Assert.Empty(invited.Items);
        Assert.True((await fixture.Sender.Send(new ResolveGroupInviteCommand(new GroupActor(Guild, Main, false), null, token, true))).IsFailure);
        Assert.True((await fixture.Sender.Send(new ResolveGroupInviteCommand(new GroupActor(Guild, Main, false), "Friday", null, true))).IsFailure);
        Assert.True((await fixture.Sender.Send(new ResolveGroupInviteCommand(new GroupActor(Guild, Twink, false), null, token, true))).IsSuccess);
        var friday = Assert.Single(await fixture.Groups.ListAsync(Guild), group => group.Name == "Friday");
        Assert.Equal(new[] { Friend, Twink }, friday.Members);
        var detail = (await fixture.Sender.Send(new GetGroupDetailQuery(new GroupActor(Guild, Main, false), friday.Id))).Value;
        Assert.Equal(new[] { Friend, Twink }, detail.Members);
        Assert.Equal(new[] { Main }, detail.LinkedAccounts[Twink]);
        Assert.Empty(friday.Invites);
        Assert.True((await fixture.Sender.Send(new InviteGroupMemberCommand(new GroupActor(Guild, Friend, false), "Friday", Twink, Guid.NewGuid()))).IsFailure);

        // Leaving from either account removes the member once.
        Assert.True((await fixture.Sender.Send(new LeaveGroupCommand(new GroupActor(Guild, Twink, false), "Friday"))).IsSuccess);
        Assert.Equal(new[] { Friend }, (await fixture.Groups.ListAsync(Guild)).Single(group => group.Name == "Friday").Members);
        Assert.True((await fixture.Sender.Send(new LeaveGroupCommand(new GroupActor(Guild, Twink, false), "Raid"))).IsFailure);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LinkingPreservesAlternatesTodayRangeOnGroupBoard(bool afterRangeEnds, bool admin)
    {
        using var fixture = await Fixture.CreateAsync();
        // Kyiv is UTC+3 on this fixture's date. The alternate alone saved 20:00–23:00.
        var start = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.FromHours(3));
        var window = await fixture.Availability.SaveAsync(Guild, Twink, null, start, start.AddHours(3));
        await fixture.Groups.UpdateAsync(Guild, "Evening", _ => new GuildGroup("Evening", Friend, [Friend, Twink, Main], []));
        var group = Assert.Single(await fixture.Groups.ListAsync(Guild));
        fixture.Clock.Advance(TimeSpan.FromHours(afterRangeEnds ? 11.5 : 8.5)); // 23:30 or 20:30 local.
        var query = new GetGroupAvailabilityBoardQuery(new GroupActor(Guild, Friend, false), group.Id, null, Kyiv);
        var before = (await fixture.Sender.Send(query)).Value;
        Assert.True(before.Rows.Single(row => row.UserId == Twink).HasData);

        if (admin)
            Assert.True((await fixture.Sender.Send(new AdminLinkAccountsCommand(Guild, Friend, Main, Twink, LinkResolutions.None))).IsSuccess);
        else
            await fixture.LinkAsync(Main, Twink);

        var board = (await fixture.Sender.Send(query)).Value;
        Assert.Equal(2, board.Rows.Length);
        // The group-specific representative stays the alternate, but reads the primary's schedule.
        var range = Assert.Single(board.Rows.Single(row => row.UserId == Twink).Ranges);
        Assert.Equal(new TimeOnly(20, 0), range.Start);
        Assert.Equal(new TimeOnly(23, 0), range.End);
        Assert.Equal(window.Id, Assert.Single(await fixture.Availability.ListAsync(Guild, Main)).Id);
        Assert.Empty(await fixture.Availability.ListAsync(Guild, Twink));
    }

    [Fact]
    public async Task MembersAndOverlapCountOncePerFamily()
    {
        using var fixture = await Fixture.CreateAsync();
        // Both accounts joined before they were linked.
        await fixture.Groups.UpdateAsync(Guild, "Friday", _ => new GuildGroup("Friday", Friend, [Friend, Main, Twink], []));
        await fixture.Availability.SaveWeeklyAsync(Guild, Main, new WeeklyAvailability(DayOfWeek.Saturday, new(18, 0), new(22, 0), Kyiv));
        await fixture.Availability.SaveWeeklyAsync(Guild, Friend, new WeeklyAvailability(DayOfWeek.Saturday, new(19, 0), new(23, 0), Kyiv));
        await fixture.LinkAsync(Main, Twink);
        Guid id = (await fixture.Groups.ListAsync(Guild)).Single().Id;

        var detail = (await fixture.Sender.Send(new GetGroupDetailQuery(new GroupActor(Guild, Friend, false), id))).Value;
        Assert.Equal(2, detail.Summary.MemberCount);
        Assert.Equal([Friend, Main], detail.Members);
        Assert.Equal([Twink], detail.LinkedAccounts[Main]);

        var board = (await fixture.Sender.Send(new GetGroupAvailabilityBoardQuery(new GroupActor(Guild, Twink, false), id, null, Kyiv))).Value;
        Assert.Equal(2, board.Rows.Length);
        Assert.Equal(2, board.FreeByHour[20]);
        Assert.Equal(1, board.FreeByHour[18]);
    }

    [Fact]
    public async Task EitherAccountEditsTheSameSchedule()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.LinkAsync(Main, Twink);

        Assert.True((await fixture.Sender.Send(new SetWeeklyAvailabilityCommand(Guild, Twink, DayOfWeek.Monday, "18:00", "22:00", null, Kyiv))).IsSuccess);
        Assert.True((await fixture.Sender.Send(new SetTodayAvailabilityCommand(Guild, Main, "", "", null, Kyiv, FullDay: true))).IsSuccess);

        var weekly = (await fixture.Sender.Send(new GetWeeklyAvailabilityQuery(Guild, Main))).Value;
        Assert.Equal(DayOfWeek.Monday, Assert.Single(weekly).Day);
        Assert.Single((await fixture.Sender.Send(new GetUpcomingAvailabilityQuery(Guild, Twink))).Value);
        Assert.Empty(await fixture.Availability.ListWeeklyAsync(Guild, Twink));
        Assert.True((await fixture.Sender.Send(new ClearWeeklyAvailabilityCommand(Guild, Main, DayOfWeek.Monday))).IsSuccess);
        Assert.Empty((await fixture.Sender.Send(new GetWeeklyAvailabilityQuery(Guild, Twink))).Value);
    }

    [Fact]
    public async Task ConflictingDataMustBeResolvedAndNothingIsSilentlyLost()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Availability.SaveWeeklyAsync(Guild, Main, new WeeklyAvailability(DayOfWeek.Monday, new(18, 0), new(22, 0), Kyiv));
        await fixture.Availability.SaveWeeklyAsync(Guild, Twink, new WeeklyAvailability(DayOfWeek.Monday, new(20, 0), new(23, 0), Kyiv));
        await fixture.Availability.SaveWeeklyAsync(Guild, Twink, new WeeklyAvailability(DayOfWeek.Tuesday, new(19, 0), new(21, 0), Kyiv));
        var main = await fixture.Availability.SaveAsync(Guild, Main, null, At(26, 18), At(26, 20));
        var clash = await fixture.Availability.SaveAsync(Guild, Twink, null, At(26, 19), At(26, 21));
        var free = await fixture.Availability.SaveAsync(Guild, Twink, null, At(27, 19), At(27, 21));
        await fixture.Groups.UpdateAsync(Guild, "Twink stack", _ => new GuildGroup("Twink stack", Twink, [Twink], []));
        await fixture.Groups.UpdateAsync(Guild, "Friday", _ => new GuildGroup("Friday", Friend, [Friend, Main], [Twink],
            new() { [Twink] = Guid.NewGuid() }));

        var request = await fixture.RequestAsync(Main);
        var preview = (await fixture.Sender.Send(new PreviewAccountLinkQuery(Guild, Twink, request.RequestId, request.Token))).Value;
        Assert.Equal(DayOfWeek.Monday, Assert.Single(preview.WeeklyConflicts).Day);
        Assert.Equal(clash.Id, Assert.Single(preview.WindowConflicts).Alternate.Id);
        Assert.Equal(["Twink stack"], preview.OrganizedByAlternate);
        Assert.Equal(["Friday"], preview.MergedInvitations);
        Assert.Equal((1, 1), (preview.WindowsMoved, preview.WeeklyDaysMoved));

        Assert.True((await fixture.ApproveAsync(Twink, request.RequestId, request.Token)).IsFailure);
        var choices = new LinkResolutions(new Dictionary<DayOfWeek, ScheduleChoice> { [DayOfWeek.Monday] = ScheduleChoice.KeepAlternate },
            new Dictionary<Guid, ScheduleChoice> { [clash.Id] = ScheduleChoice.KeepPrimary }, AcknowledgeOrganizer: true);
        Assert.True((await fixture.Sender.Send(new ApproveAccountLinkCommand(Guild, Twink, request.RequestId, request.Token, choices))).IsSuccess);

        var weekly = await fixture.Availability.ListWeeklyAsync(Guild, Main);
        Assert.Equal(new TimeOnly(20, 0), weekly.Single(day => day.Day == DayOfWeek.Monday).Start);
        Assert.Contains(weekly, day => day.Day == DayOfWeek.Tuesday);
        Assert.Equal([main.Id, free.Id], (await fixture.Availability.ListAsync(Guild, Main)).Select(window => window.Id));
        Assert.Empty(await fixture.Availability.ListAsync(Guild, Twink));
        Assert.Empty((await fixture.Groups.ListAsync(Guild)).Single(group => group.Name == "Friday").Invites);

        // The twink's own pre-link schedule (including the window that lost the conflict) is kept for unlinking.
        var snapshot = await fixture.Links.GetSnapshotAsync(Guild, Twink);
        Assert.Equal((2, 2), (snapshot!.Windows.Length, snapshot.Weekly.Length));
        Assert.Contains(snapshot.Windows, window => window.Id == clash.Id);
    }

    [Fact]
    public async Task LinkingAndUnlinkingPreserveMultipleRangesAndCertainty()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Availability.SaveWeeklyAsync(Guild, Main, new WeeklyAvailability(DayOfWeek.Monday, new(8, 0), new(9, 0), Kyiv));
        var morning = new WeeklyAvailability(DayOfWeek.Monday, new(10, 0), new(12, 0), Kyiv);
        var evening = new WeeklyAvailability(DayOfWeek.Monday, new(18, 0), new(20, 0), Kyiv, true);
        await fixture.Availability.SaveWeeklyAsync(Guild, Twink, morning);
        await fixture.Availability.SaveWeeklyAsync(Guild, Twink, evening);
        await fixture.Availability.SaveAsync(Guild, Twink, null, At(26, 18), At(26, 20), true);
        var request = await fixture.RequestAsync(Main);
        var preview = (await fixture.Sender.Send(new PreviewAccountLinkQuery(Guild, Twink, request.RequestId, request.Token))).Value;
        Assert.Equal(2, Assert.Single(preview.WeeklyConflicts).Alternate.Length);
        var choices = new LinkResolutions(new Dictionary<DayOfWeek, ScheduleChoice> { [DayOfWeek.Monday] = ScheduleChoice.KeepAlternate },
            new Dictionary<Guid, ScheduleChoice>(), AcknowledgeOrganizer: true);
        Assert.True((await fixture.Sender.Send(new ApproveAccountLinkCommand(Guild, Twink, request.RequestId, request.Token, choices))).IsSuccess);
        Assert.Equal([morning, evening], await fixture.Availability.ListWeeklyAsync(Guild, Main));
        Assert.True(Assert.Single(await fixture.Availability.ListAsync(Guild, Main)).Tentative);
        Assert.True((await fixture.Sender.Send(new UnlinkAccountCommand(Guild, Main, Twink))).IsSuccess);
        Assert.Equal([morning, evening], await fixture.Availability.ListWeeklyAsync(Guild, Twink));
        Assert.True(Assert.Single(await fixture.Availability.ListAsync(Guild, Twink)).Tentative);
        Assert.Equal([morning, evening], await fixture.Availability.ListWeeklyAsync(Guild, Main));
    }

    [Fact]
    public async Task UnlinkingKeepsSharedRecordsWithThePrimaryAndRestoresTheAlternatesOwn()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Availability.SaveWeeklyAsync(Guild, Twink, new WeeklyAvailability(DayOfWeek.Friday, new(21, 0), new(1, 0), Kyiv));
        await fixture.Groups.UpdateAsync(Guild, "Own", _ => new GuildGroup("Own", Friend, [Friend, Twink], []));
        await fixture.Groups.UpdateAsync(Guild, "Family", _ => new GuildGroup("Family", Friend, [Friend, Main], []));
        await fixture.LinkAsync(Main, Twink);
        Assert.True((await fixture.Sender.Send(new SetWeeklyAvailabilityCommand(Guild, Twink, DayOfWeek.Sunday, "10:00", "12:00", null, Kyiv))).IsSuccess);

        var preview = (await fixture.Sender.Send(new PreviewUnlinkQuery(Guild, Twink, Twink))).Value;
        Assert.Equal(["Own"], preview.AlternateKeeps);
        Assert.Equal(["Family"], preview.AlternateLoses);
        Assert.Equal((0, 1), (preview.WindowsRestored, preview.WeeklyDaysRestored));
        Assert.True((await fixture.Sender.Send(new PreviewUnlinkQuery(Guild, Friend, Twink))).IsFailure);
        Assert.True((await fixture.Sender.Send(new UnlinkAccountCommand(Guild, Stranger, Twink))).IsFailure);

        fixture.Gateway.Applied.Clear();
        Assert.True((await fixture.Sender.Send(new UnlinkAccountCommand(Guild, Main, Twink))).IsSuccess);

        Assert.Empty(await fixture.Links.ListAsync(Guild));
        var mainWeekly = await fixture.Availability.ListWeeklyAsync(Guild, Main);
        Assert.Equal(new[] { DayOfWeek.Sunday, DayOfWeek.Friday }, mainWeekly.Select(day => day.Day).Order());
        Assert.Equal(DayOfWeek.Friday, Assert.Single(await fixture.Availability.ListWeeklyAsync(Guild, Twink)).Day);
        var twink = new GroupActor(Guild, Twink, false);
        Assert.Equal(["Own"], (await fixture.Sender.Send(new GetGroupDirectoryQuery(twink, GroupDirectoryFilter.Mine, null, 1))).Value.Items.Select(group => group.Name));
        Assert.Contains(Twink, fixture.Gateway.Applied);
    }

    [Fact]
    public async Task GroupRolesFollowSavedMembershipAcrossLinkedAccounts()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Groups.UpdateAsync(Guild, "Family", _ => new GuildGroup("Family", Friend, [Friend, Main], [], RoleId: 500));
        await fixture.Groups.UpdateAsync(Guild, "Own", _ => new GuildGroup("Own", Friend, [Friend, Twink], [], RoleId: 600));

        await fixture.LinkAsync(Main, Twink);
        Assert.Equal([600UL], fixture.Gateway.ShouldHave(Twink));
        Assert.Equal([500UL], fixture.Gateway.ShouldHave(Main));

        // A manual Discord role change doesn't touch membership; repair puts the roles back from saved data.
        var moderator = new GroupActor(Guild, Friend, CanModerate: true);
        Assert.True((await fixture.Sender.Send(new RepairGroupRolesCommand(moderator, Twink))).IsSuccess);
        Assert.Contains(Twink, fixture.Gateway.Applied);
        Assert.Equal(2, (await fixture.Groups.ListAsync(Guild)).Single(group => group.Name == "Family").Members.Length);

        Assert.True((await fixture.Sender.Send(new UnlinkAccountCommand(Guild, Twink, Twink))).IsSuccess);
        Assert.Equal([600UL], fixture.Gateway.ShouldHave(Twink));
        Assert.Equal([500UL], fixture.Gateway.ShouldHave(Main));
    }

    [Fact]
    public async Task SavedLinksAreReportedWhenDiscordRolesFailToUpdate()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Groups.UpdateAsync(Guild, "Family", _ => new GuildGroup("Family", Friend, [Friend, Main], [], RoleId: 500));
        fixture.Gateway.Fail = true;

        var request = await fixture.RequestAsync(Main);
        var result = await fixture.ApproveAsync(Twink, request.RequestId, request.Token);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.RoleSyncFailed);
        Assert.Single(await fixture.Links.ListAsync(Guild));
        fixture.Gateway.Fail = false;
        Assert.True((await fixture.Sender.Send(new RepairGroupRolesCommand(new GroupActor(Guild, Friend, true), Twink))).IsSuccess);
    }

    [Fact]
    public async Task TheLinkedAccountsViewShowsTheFamilyAndOpenRequests()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.LinkAsync(Main, Twink);
        var open = await fixture.RequestAsync(Main);

        var mainView = (await fixture.Sender.Send(new GetLinkedAccountsQuery(Guild, Main))).Value;
        Assert.True(mainView.ViewerIsPrimary);
        Assert.Equal([Twink], mainView.Alternates.Select(link => link.AlternateId));
        Assert.Equal(open.RequestId, Assert.Single(mainView.Requests).Id);

        var twinkView = (await fixture.Sender.Send(new GetLinkedAccountsQuery(Guild, Twink))).Value;
        Assert.False(twinkView.ViewerIsPrimary);
        Assert.Equal(Main, twinkView.PrimaryId);
        Assert.Empty(twinkView.Requests);
        Assert.False(twinkView.CanCreateRequest);
    }

    [Fact]
    public async Task AdminDirectoryListsEveryFamilyOnlyInTheRequestedServer()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Members.Add(Stranger);
        await fixture.LinkAsync(Main, Twink);
        await fixture.LinkAsync(Main, SecondTwink);
        await fixture.LinkAsync(Friend, Stranger);

        var result = await fixture.Sender.Send(new GetServerAccountLinksQuery(Guild, Friend));
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Length);
        var main = Assert.Single(result.Value, family => family.PrimaryId == Main);
        Assert.Equal(new[] { Twink, SecondTwink }.Order(), main.Alternates.Select(link => link.AlternateId).Order());
        Assert.Equal(Stranger, Assert.Single(result.Value.Single(family => family.PrimaryId == Friend).Alternates).AlternateId);
        Assert.Empty((await fixture.Sender.Send(new GetServerAccountLinksQuery(Guild + 1, Friend))).Value);

        // Owning a linked family grants no administrative access.
        Assert.True((await fixture.Sender.Send(new GetServerAccountLinksQuery(Guild, Main))).IsFailure);
        fixture.Members.Remove(Friend);
        Assert.True((await fixture.Sender.Send(new GetServerAccountLinksQuery(Guild, Friend))).IsFailure);
    }

    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(2));

    private sealed class Fixture : IDisposable
    {
        public static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
        private SqliteTestDatabase database = null!;
        private ServiceProvider application = null!;

        public MutableClock Clock { get; } = new(Start);
        public RoleRecorder Gateway { get; private set; } = null!;
        public HashSet<ulong> Members { get; } = [Main, Twink, SecondTwink, Friend];
        public IGuildGroupStore Groups => database.Services.GetRequiredService<IGuildGroupStore>();
        public IAvailabilityStore Availability => database.Services.GetRequiredService<IAvailabilityStore>();
        public IAccountLinkStore Links => database.Services.GetRequiredService<IAccountLinkStore>();
        public ISender Sender => application.GetRequiredService<ISender>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { database = await SqliteTestDatabase.CreateAsync() };
            fixture.Gateway = new RoleRecorder(fixture.Groups);
            var settings = fixture.database.Services.GetRequiredService<IGuildSettingsStore>();
            fixture.application = new ServiceCollection().AddLogging()
                .AddSingleton(fixture.Groups).AddSingleton(settings).AddSingleton(fixture.Availability).AddSingleton(fixture.Links)
                .AddSingleton<IClock>(fixture.Clock)
                .AddSingleton<IGroupRoleGateway>(fixture.Gateway)
                .AddSingleton<IGuildMemberGateway>(new MemberCheck(fixture.Members))
                .AddApplication().BuildServiceProvider();
            return fixture;
        }

        public async Task<CreatedLinkRequest> RequestAsync(ulong primary) =>
            (await Sender.Send(new CreateAccountLinkRequestCommand(Guild, primary))).Value;

        public Task<HappyMix.Domain.Abstractions.Result<LinkResult>> ApproveAsync(ulong alternate, Guid request, string token) =>
            Sender.Send(new ApproveAccountLinkCommand(Guild, alternate, request, token, LinkResolutions.None));

        public async Task LinkAsync(ulong primary, ulong alternate)
        {
            var request = await RequestAsync(primary);
            var result = await ApproveAsync(alternate, request.RequestId, request.Token);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : null);
        }

        public void Dispose()
        {
            application?.Dispose();
            database?.Dispose();
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        private DateTimeOffset current = now;
        public DateTimeOffset UtcNow => current;
        public void Advance(TimeSpan by) => current += by;
    }

    private sealed class MemberCheck(HashSet<ulong> members) : IGuildMemberGateway
    {
        public Task<bool> IsAdministratorAsync(ulong guildId, ulong userId) => Task.FromResult(userId == Friend && members.Contains(userId));

        public Task<bool> IsMemberAsync(ulong guildId, ulong userId) => Task.FromResult(guildId == Guild && members.Contains(userId));
    }

    /// <summary>Records which accounts were reconciled and the roles each should hold after the last sync.</summary>
    private sealed class RoleRecorder(IGuildGroupStore groups) : IGroupRoleGateway
    {
        private readonly Dictionary<ulong, HashSet<ulong>> assigned = [];
        public bool Fail { get; set; }
        public List<ulong> Applied { get; } = [];

        public ulong[] ShouldHave(ulong account) => assigned.TryGetValue(account, out var roles) ? roles.ToArray() : [];

        public async Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong guildId) =>
            (await groups.ListAsync(guildId)).Select(group => group.RoleId).OfType<ulong>().ToArray();

        public Task<ulong> CreateAsync(ulong guildId, string name) => Task.FromResult(900UL);
        public Task DeleteAsync(ulong guildId, ulong roleId) => Task.CompletedTask;

        public Task ApplyAsync(ulong guildId, ulong? userId, Func<ulong, IReadOnlyCollection<ulong>, GroupRoleDelta> getDelta)
        {
            if (Fail)
                throw new GroupRoleException("Discord rejected the role operation (403).", new InvalidOperationException());
            foreach (ulong account in userId is { } one ? [one] : assigned.Keys.ToArray())
            {
                var roles = assigned.TryGetValue(account, out var held) ? held : assigned[account] = [];
                var delta = getDelta(account, roles.ToArray());
                roles.ExceptWith(delta.Remove);
                roles.UnionWith(delta.Add);
                Applied.Add(account);
            }
            return Task.CompletedTask;
        }
    }
}
