using HappyMix.Application.Abstractions.Polls;
using HappyMix.Application.Guilds;
using HappyMix.Application.Polls;
using HappyMix.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>
/// The votes block, shared by a group page and the server Votes tab. <see cref="Members"/> lists who a server vote can be
/// for; it is empty on a group page, where the group's members vote.
/// </summary>
public sealed record PollsModel(ulong GuildId, PollBoard Board, MemberNames Names, string ReturnUrl, MemberProfile[] Members,
    DateTimeOffset Now, string? Error = null)
{
    public bool IsGroup => Board.GroupId is not null;
    public string NewUrl => $"/votes/{GuildId}/new";
    public string VoteUrl(Poll poll) => $"/votes/{GuildId}/{poll.Id}/vote";
    public string CloseUrl(Poll poll) => $"/votes/{GuildId}/{poll.Id}/close";
    public bool CanClose(Poll poll) => poll.CreatorId == Board.ViewerId || Board.CanManageTags;
    public int[] Mine(Poll poll) => poll.Votes.Where(vote => vote.UserId == Board.ViewerId).Select(vote => vote.Position).ToArray();
    public int VoterCount(Poll poll) => poll.Votes.Select(vote => vote.UserId).Distinct().Count();

    /// <summary>How many people can vote, when known.</summary>
    public int? Electorate(Poll poll) => poll.GroupId is not null ? Board.Voters.Count : poll.Everyone ? null : poll.Audience.Count;
}

public sealed record VotesPage(ulong GuildId, string GuildName, bool CanConfigure, PollsModel Polls);

/// <summary>Durations offered when starting a vote, in minutes.</summary>
public static class PollDurations
{
    public static readonly (int Minutes, string Key)[] Choices =
        [(60, "Polls_In_1h"), (180, "Polls_In_3h"), (720, "Polls_In_12h"), (1440, "Polls_In_1d"), (4320, "Polls_In_3d"), (10080, "Polls_In_1w")];
}

public sealed partial class DashboardController
{
    [HttpGet("/votes/{guildId}")]
    public async Task<IActionResult> Votes(ulong guildId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, { } viewer, _))
            return NotMember();
        RememberServer(guildId);
        ViewData["NavGuildName"] = GuildName(guildId);
        var polls = await LoadPollsAsync(guildId, actor, null, $"/votes/{guildId}", includeMembers: true);
        return View("Votes", new VotesPage(guildId, GuildName(guildId), viewer.CanConfigure, polls!));
    }

    [HttpPost("/votes/{guildId}/new")]
    public async Task<IActionResult> CreatePoll(ulong guildId, Guid? groupId, Guid requestId, string? question, string[]? options,
        bool multiple, int minutes, string? audience, ulong[]? members, string? returnUrl)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        var result = await sender.Send(new CreatePollCommand(actor, groupId, question ?? "", options ?? [], multiple,
            TimeSpan.FromMinutes(minutes), members ?? [], audience == "everyone", requestId == Guid.Empty ? Guid.NewGuid() : requestId),
            HttpContext.RequestAborted);
        return PollRedirect(guildId, groupId, returnUrl, result.IsSuccess ? text["Polls_Started"].Value : null,
            result.IsFailure ? text.Message(result.Error.Description).Value : null);
    }

    [HttpPost("/votes/{guildId}/{pollId:guid}/vote")]
    public async Task<IActionResult> VotePoll(ulong guildId, Guid pollId, int[]? choice, bool clear, string? returnUrl)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        var result = await sender.Send(new VotePollCommand(actor, pollId, clear ? [] : choice ?? []), HttpContext.RequestAborted);
        return PollRedirect(guildId, result.IsSuccess ? result.Value.GroupId : null, returnUrl,
            result.IsSuccess ? text[clear || (choice ?? []).Length == 0 ? "Polls_VoteRemoved" : "Polls_Voted"].Value : null,
            result.IsFailure ? text.Message(result.Error.Description).Value : null);
    }

    [HttpPost("/votes/{guildId}/{pollId:guid}/close")]
    public async Task<IActionResult> ClosePoll(ulong guildId, Guid pollId, string? returnUrl)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        var result = await sender.Send(new ClosePollCommand(actor, pollId), HttpContext.RequestAborted);
        return PollRedirect(guildId, result.IsSuccess ? result.Value.GroupId : null, returnUrl,
            result.IsSuccess ? text["Polls_Ended"].Value : null, result.IsFailure ? text.Message(result.Error.Description).Value : null);
    }

    [HttpPost("/groups/{guildId}/{groupId:guid}/tags")]
    public async Task<IActionResult> SetGroupTags(ulong guildId, Guid groupId, string[]? tags, string? custom)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        var all = (tags ?? []).Concat((custom ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var result = await sender.Send(new SetGroupTagsCommand(actor, groupId, all), HttpContext.RequestAborted);
        if (result.IsSuccess)
            TempData["GroupBannerTitle"] = text["Group_tags_saved"].Value;
        else
            TempData["PollError"] = text.Message(result.Error.Description).Value;
        return Redirect($"/groups/{guildId}/{groupId}#settings");
    }

    /// <summary>The votes block for a group page or the server Votes tab; null when the viewer can't see the group's votes.</summary>
    private async Task<PollsModel?> LoadPollsAsync(ulong guildId, GroupActor actor, Guid? groupId, string returnUrl, bool includeMembers)
    {
        var board = await sender.Send(new GetPollBoardQuery(actor, groupId), HttpContext.RequestAborted);
        if (board.IsFailure)
            return null;
        var (members, available) = await memberDirectory.GetMembersAsync(guildId, HttpContext.RequestAborted);
        var choices = includeMembers
            ? members.Values.Where(profile => profile.InServer && !profile.IsBot && profile.Id != actor.UserId)
                .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(500).ToArray()
            : [];
        return new PollsModel(guildId, board.Value, new MemberNames(members, available), returnUrl, choices, clock.UtcNow,
            TempData["PollError"] as string);
    }

    private RedirectResult PollRedirect(ulong guildId, Guid? groupId, string? returnUrl, string? success, string? error)
    {
        if (success is not null)
            TempData["GroupBannerTitle"] = success;
        if (error is not null)
            TempData["PollError"] = error;
        string fallback = groupId is { } id ? $"/groups/{guildId}/{id}" : $"/votes/{guildId}";
        string target = returnUrl is not null && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith('/') ? returnUrl : fallback;
        return Redirect(target.Split('#')[0] + "#votes");
    }
}
