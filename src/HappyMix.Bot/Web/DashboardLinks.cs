using HappyMix.Application.Guilds;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>
/// Linked alternate accounts (twinks). The acting account always comes from the Discord sign-in, and every rule is
/// applied again by the Application handlers; form fields never name the account that is acting.
/// </summary>
public sealed partial class DashboardController
{
    [HttpGet("/links/{guildId}")]
    public async Task<IActionResult> Links(ulong guildId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        RememberServer(guildId);
        return await RenderLinksAsync(guildId, actor.UserId, viewer, TakeBanner());
    }

    [HttpPost("/links/{guildId}/requests")]
    public async Task<IActionResult> CreateLinkRequest(ulong guildId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        var result = await sender.Send(new CreateAccountLinkRequestCommand(guildId, actor.UserId), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderLinksAsync(guildId, actor.UserId, viewer, new BannerModel("error", text["Link_request_not_created"], text.Message(result.Error.Description)));

        // Shown once. Only a hash of the secret is stored.
        TempData["LinkUrl"] = $"{Request.Scheme}://{Request.Host}/links/{guildId}/approve/{result.Value.RequestId}?t={Uri.EscapeDataString(result.Value.Token)}";
        TempData["LinkExpires"] = result.Value.ExpiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Redirect($"/links/{guildId}#new-link");
    }

    [HttpPost("/links/{guildId}/requests/{requestId:guid}/revoke")]
    public async Task<IActionResult> RevokeLinkRequest(ulong guildId, Guid requestId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();

        var result = await sender.Send(new RevokeAccountLinkRequestCommand(guildId, actor.UserId, requestId), HttpContext.RequestAborted);
        TempData["GroupBannerKind"] = result.IsSuccess ? "success" : "error";
        TempData["GroupBannerTitle"] = result.IsSuccess ? text["Link_request_revoked"].Value : text.Message(result.Error.Description).Value;
        return Redirect($"/links/{guildId}");
    }

    [HttpGet("/links/{guildId}/approve/{requestId:guid}")]
    public async Task<IActionResult> ApproveLink(ulong guildId, Guid requestId, string? t)
    {
        // The signed-in account is the one that would become the alternate.
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        return await RenderApproveAsync(guildId, actor.UserId, viewer, requestId, t ?? "", null);
    }

    [HttpPost("/links/{guildId}/approve/{requestId:guid}")]
    public async Task<IActionResult> ConfirmLink(ulong guildId, Guid requestId, string? token, bool acknowledge = false)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        var result = await sender.Send(new ApproveAccountLinkCommand(guildId, actor.UserId, requestId, token ?? "",
            ReadLinkResolutions(acknowledge)), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderApproveAsync(guildId, actor.UserId, viewer, requestId, token ?? "", text.Message(result.Error.Description));

        TempData["GroupBannerTitle"] = result.Value.AlreadyLinked ? text["These_accounts_were_already_linked"].Value : text["Accounts_linked"].Value;
        TempData["GroupBannerText"] = text["HappyMix_now_treats_both_accounts_as_one_member_in_this_server"].Value;
        if (result.Value.RoleSyncFailed)
        {
            TempData["GroupBannerKind"] = "warn";
            TempData["GroupBannerText"] = text["The_link_is_saved_but_Discord_group_roles_didn_t_update_A"].Value;
        }
        return Redirect($"/links/{guildId}");
    }

    [HttpGet("/links/{guildId}/unlink/{alternateId}")]
    public async Task<IActionResult> UnlinkPreview(ulong guildId, ulong alternateId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();

        var preview = await sender.Send(new PreviewUnlinkQuery(guildId, actor.UserId, alternateId), HttpContext.RequestAborted);
        if (preview.IsFailure)
        {
            TempData["GroupBannerKind"] = "error";
            TempData["GroupBannerTitle"] = text.Message(preview.Error.Description).Value;
            return Redirect($"/links/{guildId}");
        }

        var (members, available) = await memberDirectory.GetMembersAsync(guildId, HttpContext.RequestAborted);
        return View("Unlink", new UnlinkPage(guildId, GuildName(guildId), viewer, preview.Value, new MemberNames(members, available)));
    }

    [HttpPost("/links/{guildId}/unlink/{alternateId}")]
    public async Task<IActionResult> Unlink(ulong guildId, ulong alternateId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();

        var result = await sender.Send(new UnlinkAccountCommand(guildId, actor.UserId, alternateId), HttpContext.RequestAborted);
        if (result.IsFailure)
        {
            TempData["GroupBannerKind"] = "error";
            TempData["GroupBannerTitle"] = text.Message(result.Error.Description).Value;
        }
        else
        {
            TempData["GroupBannerTitle"] = text["Accounts_unlinked"].Value;
            TempData["GroupBannerText"] = text["The_alternate_keeps_its_own_memberships_and_gets_its_saved_schedule_back"].Value;
            if (result.Value.RoleSyncFailed)
            {
                TempData["GroupBannerKind"] = "warn";
                TempData["GroupBannerText"] = text["The_accounts_are_unlinked_but_Discord_group_roles_didn_t_update_A"].Value;
            }
        }
        return Redirect($"/links/{guildId}");
    }

    private async Task<IActionResult> RenderLinksAsync(ulong guildId, ulong userId, GroupViewer viewer, BannerModel? banner)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var view = (await sender.Send(new GetLinkedAccountsQuery(guildId, userId), cancellationToken)).Value;
        var (members, available) = await memberDirectory.GetMembersAsync(guildId, cancellationToken);
        var zone = await GetGuildZoneAsync(guildId, cancellationToken);
        DateTimeOffset? expires = TempData["LinkExpires"] is string raw && long.TryParse(raw, out long seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return View("LinkedAccounts", new LinksPage(guildId, GuildName(guildId), viewer, view, new MemberNames(members, available),
            TempData["LinkUrl"] as string, expires, banner, zone.Id));
    }

    private async Task<IActionResult> RenderApproveAsync(ulong guildId, ulong userId, GroupViewer viewer, Guid requestId, string token,
        string? error)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var (members, available) = await memberDirectory.GetMembersAsync(guildId, cancellationToken);
        var zone = await GetGuildZoneAsync(guildId, cancellationToken);
        var preview = await sender.Send(new PreviewAccountLinkQuery(guildId, userId, requestId, token), cancellationToken);
        string? problem = error ?? (preview.IsFailure ? text.Message(preview.Error.Description).Value : null);
        bool sameAccount = preview.IsFailure && preview.Error.Description.StartsWith("You're signed in with the account that created", StringComparison.Ordinal);
        var view = View("ApproveLink", new ApproveLinkPage(guildId, GuildName(guildId), viewer, requestId, token,
            preview.IsSuccess ? preview.Value : null, problem, sameAccount, new MemberNames(members, available), zone.Id));
        if (problem is not null)
            view.StatusCode = preview.IsSuccess ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status400BadRequest;
        return view;
    }

    private LinkResolutions ReadLinkResolutions(bool acknowledge)
    {
        var weekly = new Dictionary<DayOfWeek, ScheduleChoice>();
        var windows = new Dictionary<Guid, ScheduleChoice>();
        foreach (var (key, value) in Request.Form)
        {
            var choice = value.ToString() == "alternate" ? ScheduleChoice.KeepAlternate : ScheduleChoice.KeepPrimary;
            if (key.StartsWith("weekly-", StringComparison.Ordinal) && Enum.TryParse<DayOfWeek>(key[7..], out var day))
                weekly[day] = choice;
            else if (key.StartsWith("window-", StringComparison.Ordinal) && Guid.TryParse(key[7..], out var id))
                windows[id] = choice;
        }

        return new LinkResolutions(weekly, windows, acknowledge);
    }

    [HttpGet("/links/{guildId}/admin")]
    public async Task<IActionResult> AdminLinks(ulong guildId, ulong primary = 0, ulong alternate = 0)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { CanConfigure: true } viewer, _))
            return StatusCode(StatusCodes.Status403Forbidden);

        return await RenderAdminLinksAsync(guildId, actor.UserId, viewer, primary, alternate, null);
    }

    [HttpPost("/links/{guildId}/admin")]
    public async Task<IActionResult> SaveAdminLink(ulong guildId, ulong primary, ulong alternate, bool acknowledge)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { CanConfigure: true } viewer, _))
            return StatusCode(StatusCodes.Status403Forbidden);

        var result = await sender.Send(new AdminLinkAccountsCommand(guildId, actor.UserId, primary, alternate,
            ReadLinkResolutions(acknowledge)), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderAdminLinksAsync(guildId, actor.UserId, viewer, primary, alternate, text.Message(result.Error.Description));

        TempData["GroupBannerTitle"] = text["Accounts_linked"].Value;
        if (result.Value.RoleSyncFailed)
        {
            TempData["GroupBannerKind"] = "warn";
            TempData["GroupBannerText"] = text["The_link_is_saved_but_Discord_group_roles_didn_t_update_A"].Value;
        }
        return Redirect($"/links/{guildId}/admin");
    }

    private async Task<IActionResult> RenderAdminLinksAsync(ulong guildId, ulong administrator, GroupViewer viewer,
        ulong primary, ulong alternate, string? error)
    {
        var ct = HttpContext.RequestAborted;
        var (members, available) = await memberDirectory.GetMembersAsync(guildId, ct);
        var directory = await sender.Send(new GetServerAccountLinksQuery(guildId, administrator), ct);
        if (directory.IsFailure)
            return StatusCode(StatusCodes.Status403Forbidden);
        var candidates = members.Values.Where(member => member.InServer && !member.IsBot)
            .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ViewData["AdminMode"] = true;
        ViewData["Banner"] = TakeBanner();
        LinkPreview? preview = null;
        if (primary != 0 && alternate != 0)
        {
            var result = await sender.Send(new PreviewAdminAccountLinkQuery(guildId, administrator, primary, alternate), ct);
            if (result.IsSuccess)
                preview = result.Value;
            else
                error ??= text.Message(result.Error.Description);
        }
        var zone = await GetGuildZoneAsync(guildId, ct);
        return View("AdminLinks", new AdminLinksPage(new ApproveLinkPage(guildId, GuildName(guildId), viewer, Guid.Empty, "",
            preview, error, false, new MemberNames(members, available), zone.Id), candidates, directory.Value));
    }
}
