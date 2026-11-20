using System.Globalization;
using HappyMix.Application.Guilds;
using HappyMix.Bot.Services;
using HappyMix.Domain.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>
/// Groups workspace. The controller only resolves who is acting (Discord session + live server membership),
/// reads input and renders; every permission is enforced again in the Application handlers.
/// </summary>
public sealed partial class DashboardController
{
    private const int GroupPageSize = 12;

    [HttpGet("/groups/{guildId}")]
    public async Task<IActionResult> Groups(ulong guildId, string? filter, string? q, int page = 1)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, { } roles))
            return NotMember();

        RememberServer(guildId);
        var chosen = ParseFilter(filter);
        var cancellationToken = HttpContext.RequestAborted;
        var directory = (await sender.Send(new GetGroupDirectoryQuery(actor, chosen, q, page, GroupPageSize), cancellationToken)).Value;
        // First visit with nothing of your own: show what you can access instead of an empty "My groups".
        if (filter is null && directory.MineCount == 0 && directory.InvitedCount > 0)
        {
            chosen = GroupDirectoryFilter.Invited;
            directory = (await sender.Send(new GetGroupDirectoryQuery(actor, chosen, q, page, GroupPageSize), cancellationToken)).Value;
        }

        bool canCreate = (await sender.Send(new CanCreateGroupQuery(actor, roles), cancellationToken)).Value;
        var (members, available) = await memberDirectory.GetMembersAsync(guildId, cancellationToken);
        var tags = (await sender.Send(new HappyMix.Application.Polls.GetGroupTagsQuery(guildId), cancellationToken)).Value;
        return View("Groups", new GroupsPage(guildId, GuildName(guildId), viewer, canCreate, chosen, q?.Trim(), directory,
            new MemberNames(members, available), TakeBanner()) { Tags = tags });
    }

    [HttpGet("/groups/{guildId}/new")]
    public async Task<IActionResult> NewGroup(ulong guildId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, { } roles))
            return NotMember();

        bool canCreate = (await sender.Send(new CanCreateGroupQuery(actor, roles), HttpContext.RequestAborted)).Value;
        return View("NewGroup", new CreateGroupPage(guildId, GuildName(guildId), viewer, canCreate, null, null, [], [], null, null));
    }

    [HttpPost("/groups/{guildId}/new")]
    public async Task<IActionResult> CreateGroup(ulong guildId, string? name, string? description, string[]? gameName, string[]? gameSteam)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, { } roles))
            return NotMember();

        gameName ??= [];
        gameSteam ??= [];
        var inputs = gameName.Select((game, index) => new GroupGameInput(game ?? "", index < gameSteam.Length ? gameSteam[index] : null))
            .Where(game => !string.IsNullOrWhiteSpace(game.Name) || !string.IsNullOrWhiteSpace(game.Steam))
            .Take(CreateGroupPage.GameRows)
            .ToArray();
        var result = await sender.Send(new CreateGroupCommand(actor, name ?? "", description, roles, inputs), HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            string message = result.Error.Description;
            string field = message.Contains("game", StringComparison.OrdinalIgnoreCase) || message.Contains("Steam", StringComparison.Ordinal)
                ? "games" : message.Contains("description", StringComparison.OrdinalIgnoreCase) ? "description"
                : message.Contains("name", StringComparison.OrdinalIgnoreCase) ? "name" : "";
            bool canCreate = (await sender.Send(new CanCreateGroupQuery(actor, roles), HttpContext.RequestAborted)).Value;
            var view = View("NewGroup", new CreateGroupPage(guildId, GuildName(guildId), viewer, canCreate, name, description,
                gameName, gameSteam, text.Message(message), field));
            view.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return view;
        }

        SetSavedBanner(result.Value, text["Group_created"], text["You_organize_this_group_Invite_members_when_you_re_ready"]);
        return Redirect($"/groups/{guildId}/{result.Value.GroupId}");
    }

    [HttpGet("/groups/{guildId}/{groupId:guid}")]
    public async Task<IActionResult> Group(ulong guildId, Guid groupId, string? back, string? day, string? hours, string? tab)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        RememberServer(guildId);
        DateOnly? date = DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : null;
        return await RenderGroupAsync(guildId, actor, viewer, groupId, back, TakeBanner(), null, date,
            evening: hours == "12-24", tab: tab == "roster" ? "roster" : null);
    }

    [HttpPost("/groups/{guildId}/{groupId:guid}/invitation")]
    public Task<IActionResult> RespondToInvitation(ulong guildId, Guid groupId, string decision) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new ResolveGroupInviteCommand(actor, group.Name, null, decision == "accept"),
            decision == "accept" ? "You joined the group" : "Invitation declined", afterDecline: decision != "accept");

    [HttpPost("/groups/{guildId}/{groupId:guid}/leave")]
    public Task<IActionResult> LeaveGroup(ulong guildId, Guid groupId) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new LeaveGroupCommand(actor, group.Name), "You left the group",
            afterDecline: true);

    [HttpPost("/groups/{guildId}/{groupId:guid}/description")]
    public Task<IActionResult> EditGroupDescription(ulong guildId, Guid groupId, string? description) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new EditGroupDescriptionCommand(actor, group.Name, description),
            "Description saved", section: "settings", form: new GroupFormState("settings", null, Description: description));

    [HttpPost("/groups/{guildId}/{groupId:guid}/invite")]
    public async Task<IActionResult> InviteToGroup(ulong guildId, Guid groupId, string? member)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        var detail = await sender.Send(new GetGroupDetailQuery(actor, groupId), HttpContext.RequestAborted);
        if (detail.IsFailure)
            return GroupNotFound(guildId);

        var target = await ResolveInviteeAsync(guildId, member);
        if (target is null)
            return await RenderGroupAsync(guildId, actor, viewer, groupId, null, null, new GroupFormState("members",
                text["Choose_a_person_from_this_server_by_their_Discord_username"], Member: member));

        Guid token = Guid.NewGuid();
        var group = detail.Value.Summary;
        var result = await sender.Send(new InviteGroupMemberCommand(actor, group.Name, target.Value.Id, token), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderGroupAsync(guildId, actor, viewer, groupId, null, null,
                new GroupFormState("members", text.Message(result.Error.Description), Member: member));

        bool delivered = discord.GetGuild(guildId) is { } guild &&
            await GroupInviteMessages.SendAsync(target.Value.User, guildId, guild.Name, group.Name, token);
        TempData["GroupBannerTitle"] = text["Invitation_sent_to_Arg0", target.Value.Name].Value;
        TempData["GroupBannerText"] = delivered
            ? text["They_got_a_Discord_message_with_Accept_and_Decline_buttons_and_can"].Value
            : text["Discord_couldn_t_deliver_the_message_They_can_still_accept_from_Groups"].Value;
        return Redirect($"/groups/{guildId}/{groupId}?tab=roster#members");
    }

    [HttpPost("/groups/{guildId}/{groupId:guid}/members/{memberId}/remove")]
    public Task<IActionResult> RemoveGroupMember(ulong guildId, Guid groupId, ulong memberId, string? kind) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new RemoveGroupMemberCommand(actor, group.Name, memberId),
            kind == "invite" ? "Invitation revoked" : "Member removed", section: "members");

    [HttpPost("/groups/{guildId}/{groupId:guid}/members/{memberId}/repair")]
    public Task<IActionResult> RepairMemberRoles(ulong guildId, Guid groupId, ulong memberId) =>
        RunGroupActionAsync(guildId, groupId, (actor, _) => new RepairGroupRolesCommand(actor, memberId),
            "Roles repaired for this member", section: "members");

    [HttpPost("/groups/{guildId}/{groupId:guid}/games")]
    public Task<IActionResult> AddGroupGame(ulong guildId, Guid groupId, string? name, string? steam, string? provider, string? externalId) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new AddGroupGameCommand(actor, group.Id, name ?? "", steam, provider, externalId),
            "Game added", section: "games", form: new GroupFormState("games", null, name, steam));

    [HttpPost("/groups/{guildId}/{groupId:guid}/games/{gameId:guid}")]
    public Task<IActionResult> UpdateGroupGame(ulong guildId, Guid groupId, Guid gameId, string? name, string? steam) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new UpdateGroupGameCommand(actor, group.Id, gameId, name ?? "", steam),
            "Game updated", section: "games", form: new GroupFormState("game-" + gameId.ToString("N"), null, name, steam));

    [HttpPost("/groups/{guildId}/{groupId:guid}/games/{gameId:guid}/remove")]
    public Task<IActionResult> RemoveGroupGame(ulong guildId, Guid groupId, Guid gameId) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new RemoveGroupGameCommand(actor, group.Id, gameId),
            "Game removed", section: "games");

    [HttpPost("/groups/{guildId}/{groupId:guid}/live")]
    public Task<IActionResult> SetLiveSharing(ulong guildId, Guid groupId, bool share, bool notify) =>
        RunLiveActionAsync(guildId, groupId, actor => new HappyMix.Application.Live.SetLiveSharingCommand(actor, groupId, share, notify));

    [HttpPost("/groups/{guildId}/{groupId:guid}/live/channel")]
    public Task<IActionResult> SetLiveChannel(ulong guildId, Guid groupId, bool post) =>
        RunLiveActionAsync(guildId, groupId, actor => new HappyMix.Application.Live.SetLiveChannelCommand(actor, groupId, post));

    private async Task<IActionResult> RunLiveActionAsync(ulong guildId, Guid groupId,
        Func<GroupActor, MediatR.IRequest<HappyMix.Domain.Abstractions.Result<HappyMix.Application.Live.GroupLive>>> command)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();
        var result = await sender.Send(command(actor), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderGroupAsync(guildId, actor, viewer, groupId, null, null, new GroupFormState("live", text.Message(result.Error.Description)));
        TempData["GroupBannerTitle"] = text["Live_Saved"].Value;
        return Redirect($"/groups/{guildId}/{groupId}#live");
    }

    private async Task<LiveModel?> LoadLiveAsync(ulong guildId, GroupActor actor, GroupDetail group, CancellationToken cancellationToken)
    {
        if (!group.CanViewRoster
            || await sender.Send(new HappyMix.Application.Live.GetGroupLiveQuery(actor, group.Summary.Id), cancellationToken) is not { IsSuccess: true } live)
            return null;
        var steam = (await sender.Send(new HappyMix.Application.Players.GetMyGamesQuery(actor.UserId), cancellationToken)).Value?.Steam;
        return new LiveModel(live.Value, await GetGuildZoneAsync(guildId, cancellationToken), clock.UtcNow,
            group.Summary.Relation is GroupRelation.Member or GroupRelation.Organizer, steam?.Verified == true);
    }

    [HttpPost("/groups/{guildId}/{groupId:guid}/close")]
    public async Task<IActionResult> CloseGroup(ulong guildId, Guid groupId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        var detail = await sender.Send(new GetGroupDetailQuery(actor, groupId), HttpContext.RequestAborted);
        if (detail.IsFailure)
            return GroupNotFound(guildId);

        var result = await sender.Send(new DeleteGroupCommand(actor, detail.Value.Summary.Name), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderGroupAsync(guildId, actor, viewer, groupId, null, null,
                new GroupFormState("close", text.Message(result.Error.Description)));

        TempData["GroupBannerTitle"] = text["Arg0_was_closed", detail.Value.Summary.Name].Value;
        TempData["GroupBannerText"] = text["Its_saved_record_and_Discord_role_were_deleted"].Value;
        return Redirect($"/groups/{guildId}");
    }

    [HttpPost("/groups/{guildId}/repair-roles")]
    public async Task<IActionResult> RepairAllGroupRoles(ulong guildId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();

        var result = await sender.Send(new RepairGroupRolesCommand(actor, null), HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            TempData["GroupBannerKind"] = "error";
            TempData["GroupBannerTitle"] = text["Role_repair_failed"].Value;
            TempData["GroupBannerText"] = text.Message(result.Error.Description).Value;
        }
        else
        {
            TempData["GroupBannerTitle"] = text["Group_roles_repaired"].Value;
            TempData["GroupBannerText"] = text["Every_member_s_group_roles_now_match_the_saved_groups"].Value;
        }
        return Redirect($"/groups/{guildId}?filter=all");
    }

    /// <summary>Looks the group up through the visibility-checked query, then runs one name-scoped command.</summary>
    /// <summary>Platforms and covers of games picked from IGDB (cached catalog lookups).</summary>
    private async Task<Dictionary<Guid, HappyMix.Application.Abstractions.Media.MediaInfo>> GameDetailsAsync(
        IEnumerable<HappyMix.Application.Abstractions.Guilds.GroupGame> games, CancellationToken cancellationToken)
    {
        var catalog = HttpContext.RequestServices.GetRequiredService<HappyMix.Application.Abstractions.Media.IMediaCatalog>();
        var details = new Dictionary<Guid, HappyMix.Application.Abstractions.Media.MediaInfo>();
        foreach (var game in games.Where(game => game.Provider == HappyMix.Application.Abstractions.Guilds.GroupGame.IgdbProvider && game.ExternalId is not null))
            if (await catalog.GetAsync(new(game.Provider!, game.ExternalId!), cancellationToken) is { } info)
                details[game.Id] = info;
        return details;
    }

    private async Task<IActionResult> RunGroupActionAsync(ulong guildId, Guid groupId,
        Func<GroupActor, GroupSummary, GroupCommand> command, string success, string section = "overview",
        GroupFormState? form = null, bool afterDecline = false)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        var detail = await sender.Send(new GetGroupDetailQuery(actor, groupId), HttpContext.RequestAborted);
        if (detail.IsFailure)
            return GroupNotFound(guildId);

        var result = await sender.Send(command(actor, detail.Value.Summary), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderGroupAsync(guildId, actor, viewer, groupId, null, null,
                (form ?? new GroupFormState(section, null)) with { Section = form?.Section ?? section, Error = text.Message(result.Error.Description) });

        SetSavedBanner(result.Value, text.Message(success), null);
        // After declining or leaving, the group may no longer be visible to this user.
        return Redirect(afterDecline ? $"/groups/{guildId}"
            : $"/groups/{guildId}/{groupId}{(section == "members" ? "?tab=roster" : "")}#{section}");
    }

    private async Task<IActionResult> RenderGroupAsync(ulong guildId, GroupActor actor, GroupViewer viewer, Guid groupId,
        string? back, BannerModel? banner, GroupFormState? form, DateOnly? day = null, bool evening = false, string? tab = null)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var detail = await sender.Send(new GetGroupDetailQuery(actor, groupId), cancellationToken);
        if (detail.IsFailure)
            return GroupNotFound(guildId);

        // The board query applies the roster rule again, so only people who may see members get it.
        var board = detail.Value.CanViewRoster
            ? (await sender.Send(new GetGroupAvailabilityBoardQuery(actor, groupId, day, options.Value.TimeZoneId), cancellationToken)) is { IsSuccess: true } loaded
                ? loaded.Value : null
            : null;

        var (members, available) = await memberDirectory.GetMembersAsync(guildId, cancellationToken);
        var group = detail.Value;
        var taken = group.Members.Concat(group.Invites).ToHashSet();
        foreach (var accounts in group.LinkedAccounts.Values)
            taken.UnionWith(accounts);
        var candidates = group.Summary.CanManage
            ? members.Values.Where(profile => profile.InServer && !profile.IsBot && !taken.Contains(profile.Id))
                .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(500).ToArray()
            : [];
        string backUrl = back is not null && back.StartsWith($"/groups/{guildId}", StringComparison.Ordinal) && Url.IsLocalUrl(back)
            ? back : $"/groups/{guildId}";
        var names = new MemberNames(members, available);
        var events = Array.Empty<EventRow>();
        if (detail.Value.CanViewRoster)
        {
            // Every upcoming date of the group's events, so pending plans, confirmed sessions and cancellations show here.
            var now = clock.UtcNow;
            var viewerId = (await sender.Send(new HappyMix.Application.Events.GetMemberEventsQuery(guildId, actor.UserId), cancellationToken)).Value.ViewerId;
            events = (await sender.Send(new HappyMix.Application.Events.GetGroupEventsQuery(guildId, groupId), cancellationToken)).Value
                .SelectMany(details => details.Occurrences.Where(item => item.End > now)
                    .Select(item => EventText.Row(details, item, viewerId, EventText.Zone(details.Event), null, id => names.Name(id, key => text.Message(key)), guildId)))
                .OrderBy(row => row.Date).Take(10).ToArray();
        }
        var polls = detail.Value.CanViewRoster ? await LoadPollsAsync(guildId, actor, groupId, $"/groups/{guildId}/{groupId}", includeMembers: false) : null;
        var watch = detail.Value.CanViewRoster ? await LoadWatchlistAsync(guildId, actor, groupId) : null;
        if (watch is not null && watch.Board.Entries.Count == 0 && polls?.Board.Tags.Any(tag => tag is "series" or "films" or "anime") != true)
            watch = null;
        GroupNotificationSummary? notifications = null;
        var notificationSettings = HttpContext.RequestServices.GetRequiredService<HappyMix.Bot.Services.EventNotificationSettings>();
        if (await GetDiscordMemberAsync(guildId) is { } member && await notificationSettings.CanManageAsync(member, "group", groupId) &&
            await notificationSettings.GetEffectiveAsync(guildId, "group", groupId) is { } effective)
        {
            var route = effective.Route;
            string? channel = discord.GetGuild(guildId)?.GetTextChannel(route.Channel()) is { } found ? "#" + found.Name : null;
            notifications = new GroupNotificationSummary(route.Enabled, channel, LanguageName(route.Language),
                route.ReminderMinutes ?? notificationSettings.DefaultReminderMinutes, route.Updates, $"/notifications/{guildId}/group/{groupId}");
        }
        var view = View("Group", new GroupPage(guildId, GuildName(guildId), viewer, group, names,
            candidates, banner, backUrl, form, board, evening, tab)
        {
            Notifications = notifications,
            Polls = polls,
            GameDetails = await GameDetailsAsync(group.Games, cancellationToken),
            Interest = group.CanViewRoster && await sender.Send(new HappyMix.Application.Players.GetGroupGameInterestQuery(actor, groupId), cancellationToken)
                is { IsSuccess: true } interest ? interest.Value : null,
            Live = await LoadLiveAsync(guildId, actor, group, cancellationToken),
            Tags = (await sender.Send(new HappyMix.Application.Polls.GetGroupTagsQuery(guildId), cancellationToken)).Value.GetValueOrDefault(groupId) ?? [],
            Watch = watch,
            Events = events,
            BoardEvents = board is null ? BoardEvents.Empty
                : await LoadBoardEventsAsync(guildId, actor.UserId, board.Rows.Select(row => row.UserId), board.Date, board.TimeZone)
        });
        if (form?.Error is not null)
            view.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return view;
    }

    private async Task<(ulong Id, string Name, Discord.IUser User)?> ResolveInviteeAsync(ulong guildId, string? input)
    {
        string value = input?.Trim().TrimStart('@') ?? "";
        if (value.Length == 0 || discord.GetGuild(guildId) is null)
            return null;

        var (members, _) = await memberDirectory.GetMembersAsync(guildId, HttpContext.RequestAborted);
        var profile = members.Values.FirstOrDefault(item => string.Equals(item.Username, value, StringComparison.OrdinalIgnoreCase));
        ulong id = profile?.Id ?? (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) ? parsed : 0);
        if (id == 0)
            return null;

        // Confirm with Discord that the person is a human member of this server right now.
        var guild = await discord.Rest.GetGuildAsync(guildId);
        var user = await guild.GetUserAsync(id);
        return user is null || user.IsBot ? null : (user.Id, user.DisplayName, user);
    }

    private async Task<(GroupActor Actor, GroupViewer Viewer, ulong[] Roles)?> GetGroupActorAsync(ulong guildId)
    {
        // Re-checked on every request: a session alone never grants access to a server's groups.
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return null;

        var permissions = member.GuildPermissions;
        bool moderate = permissions.ManageGuild || permissions.Administrator;
        return (new GroupActor(guildId, member.Id, moderate), new GroupViewer(member.Id, moderate, permissions.Administrator),
            member.RoleIds.ToArray());
    }

    private void SetSavedBanner(GroupChange change, string title, string? body)
    {
        TempData["GroupBannerTitle"] = title;
        TempData["GroupBannerText"] = body;
        if (change.RoleSyncFailed)
        {
            TempData["GroupBannerKind"] = "warn";
            TempData["GroupBannerTitle"] = text["Saved_but_the_Discord_role_didn_t_update"].Value;
            TempData["GroupBannerText"] = text["The_group_record_is_saved_A_server_moderator_can_fix_the_role"].Value;
        }
    }

    private BannerModel? TakeBanner() => TempData["GroupBannerTitle"] is string title
        ? new BannerModel(TempData["GroupBannerKind"] as string ?? "success", title, TempData["GroupBannerText"] as string)
        : null;

    private ViewResult GroupNotFound(ulong guildId) => Forbidden(new ForbiddenPage(text["Group_not_available"],
        text["That_group_doesn_t_exist_or_you_don_t_have_access_to"], $"/groups/{guildId}", text["Back_to_groups"]));

    private string GuildName(ulong guildId) => discord.GetGuild(guildId)?.Name ?? "HappyMix";

    private static GroupDirectoryFilter ParseFilter(string? filter) => filter switch
    {
        "open" => GroupDirectoryFilter.Open,
        "invited" => GroupDirectoryFilter.Invited,
        "all" => GroupDirectoryFilter.All,
        _ => GroupDirectoryFilter.Mine
    };
}
