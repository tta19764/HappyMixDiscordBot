using Discord;
using Poll = HappyMix.Application.Abstractions.Polls.Poll;
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
    /// <summary>Channels a server vote can be posted in (empty on a group page).</summary>
    public IReadOnlyList<(ulong Id, string Name)> Channels { get; init; } = [];

    /// <summary>Where a group's votes are posted: its notification channel, when notifications are on.</summary>
    public string? GroupChannel { get; init; }

    /// <summary>The language for option details (seasons, episodes, minutes).</summary>
    public string Language { get; init; } = "en";

    /// <summary>The server's time zone, for "today" and "this week" results.</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;

    public bool IsGroup => Board.GroupId is not null;
    public string NewUrl => $"/votes/{GuildId}/new";
    public string BuyUrl => $"/votes/{GuildId}/buy";

    /// <summary>Games offered for a buy vote: the group's games and the ones members want, from the suggestions.</summary>
    public IEnumerable<PollSuggestion> BuyChoices => Board.Suggestions.Where(item => item.Kind == "game");
    public string VoteUrl(Poll poll) => $"/votes/{GuildId}/{poll.Id}/vote";
    public string CloseUrl(Poll poll) => $"/votes/{GuildId}/{poll.Id}/close";
    public bool CanClose(Poll poll) => poll.CreatorId == Board.ViewerId || Board.CanManageTags;
    public int[] Mine(Poll poll) => poll.Votes.Where(vote => vote.UserId == Board.ViewerId).Select(vote => vote.Position).ToArray();
    public int VoterCount(Poll poll) => poll.Votes.Select(vote => vote.UserId).Distinct().Count();

    /// <summary>A link that opens the vote's Discord message, once it is posted.</summary>
    public string? DiscordUrl(Poll poll) => poll.ChannelId is { } channel && poll.MessageId is { } message
        ? $"https://discord.com/channels/{GuildId}/{channel}/{message}" : null;

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
        bool multiple, int minutes, string? audience, ulong[]? members, ulong? channelId, string? returnUrl,
        [FromServices] PollMessages messages)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        // Group votes go to the group's notification channel; server votes to the channel picked in the form, if any.
        ulong? channel = groupId is { } group ? await messages.GroupChannelAsync(guildId, group)
            : channelId is > 0 and var picked && PostableChannels(guildId, actor.UserId).Any(item => item.Id == picked) ? picked : null;
        var result = await sender.Send(new CreatePollCommand(actor, groupId, question ?? "", options ?? [], multiple,
            TimeSpan.FromMinutes(minutes), members ?? [], audience == "everyone", requestId == Guid.Empty ? Guid.NewGuid() : requestId, channel),
            HttpContext.RequestAborted);
        if (result.IsSuccess)
            await SyncPollAsync(messages, guildId, result.Value);
        return PollRedirect(guildId, groupId, returnUrl, result.IsSuccess ? text["Polls_Started"].Value : null,
            result.IsFailure ? text.Message(result.Error.Description).Value : null, result.IsSuccess ? "active" : "new");
    }

    /// <summary>"Who will buy this game": a group vote with four fixed answers; Steam owners are counted at once.</summary>
    [HttpPost("/votes/{guildId}/buy")]
    public async Task<IActionResult> CreateBuyVote(ulong guildId, Guid groupId, Guid requestId, string? game, int minutes, string? returnUrl,
        [FromServices] PollMessages messages)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        ulong? channel = await messages.GroupChannelAsync(guildId, groupId);
        var result = await sender.Send(new CreateBuyVoteCommand(actor, groupId, game ?? "", text["Buy_Question"].Value, TimeSpan.FromMinutes(minutes),
            requestId == Guid.Empty ? Guid.NewGuid() : requestId, channel), HttpContext.RequestAborted);
        if (result.IsSuccess)
            await SyncPollAsync(messages, guildId, result.Value);
        return PollRedirect(guildId, groupId, returnUrl, result.IsSuccess ? text["Polls_Started"].Value : null,
            result.IsFailure ? text.Message(result.Error.Description).Value : null, result.IsSuccess ? "active" : "new");
    }

    [HttpPost("/votes/{guildId}/{pollId:guid}/vote")]
    public async Task<IActionResult> VotePoll(ulong guildId, Guid pollId, int[]? choice, bool clear, string? returnUrl,
        [FromServices] PollMessages messages)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        var result = await sender.Send(new VotePollCommand(actor, pollId, clear ? [] : choice ?? []), HttpContext.RequestAborted);
        if (result.IsSuccess)
            await SyncPollAsync(messages, guildId, pollId);
        return PollRedirect(guildId, result.IsSuccess ? result.Value.GroupId : null, returnUrl,
            result.IsSuccess ? text[clear || (choice ?? []).Length == 0 ? "Polls_VoteRemoved" : "Polls_Voted"].Value : null,
            result.IsFailure ? text.Message(result.Error.Description).Value : null);
    }

    [HttpPost("/votes/{guildId}/{pollId:guid}/close")]
    public async Task<IActionResult> ClosePoll(ulong guildId, Guid pollId, string? returnUrl, [FromServices] PollMessages messages)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        var result = await sender.Send(new ClosePollCommand(actor, pollId), HttpContext.RequestAborted);
        if (result.IsSuccess)
            await SyncPollAsync(messages, guildId, pollId);
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
        string? groupChannel = groupId is { } group
            && await HttpContext.RequestServices.GetRequiredService<PollMessages>().GroupChannelAsync(guildId, group) is { } channelId
            && discord.GetGuild(guildId)?.GetTextChannel(channelId) is { } found ? "#" + found.Name : null;
        return new PollsModel(guildId, board.Value, new MemberNames(members, available), returnUrl, choices, clock.UtcNow,
            TempData["PollError"] as string)
        {
            Channels = includeMembers ? PostableChannels(guildId, actor.UserId) : [],
            GroupChannel = groupChannel,
            Language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            Zone = await ServerZoneAsync(guildId)
        };
    }

    private async Task<TimeZoneInfo> ServerZoneAsync(ulong guildId)
    {
        string zoneId = (await sender.Send(new HappyMix.Application.Guilds.GetGuildSettingsQuery(guildId), HttpContext.RequestAborted)).Value?.TimeZoneId
            ?? options.Value.TimeZoneId;
        return TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>Text channels the member can see and the bot can post vote messages in.</summary>
    private (ulong Id, string Name)[] PostableChannels(ulong guildId, ulong userId)
    {
        if (discord.GetGuild(guildId) is not { } guild || guild.GetUser(userId) is not { } member)
            return [];
        return guild.TextChannels
            .Where(channel => channel.GetChannelType() == ChannelType.Text && member.GetPermissions(channel).ViewChannel &&
                guild.CurrentUser.GetPermissions(channel) is { ViewChannel: true, SendMessages: true, EmbedLinks: true })
            .OrderBy(channel => channel.Position).Select(channel => (channel.Id, channel.Name)).ToArray();
    }

    /// <summary>Posts or updates the vote's Discord message. Discord trouble never fails the dashboard action.</summary>
    private async Task SyncPollAsync(PollMessages messages, ulong guildId, Guid pollId)
    {
        try
        {
            await messages.SyncAsync(pollId, guildId, HttpContext.RequestAborted);
        }
        catch (Exception error) when (error is Discord.Net.HttpException or TimeoutException or InvalidOperationException)
        {
            // The catch-up job retries posting; the vote itself is saved.
        }
    }

    /// <summary>Back to the votes, on the tab that fits: Active after voting or starting a vote, New vote after a failed start.</summary>
    private RedirectResult PollRedirect(ulong guildId, Guid? groupId, string? returnUrl, string? success, string? error, string tab = "active")
    {
        if (success is not null)
            TempData["GroupBannerTitle"] = success;
        if (error is not null)
            TempData["PollError"] = error;
        string fallback = groupId is { } id ? $"/groups/{guildId}/{id}" : $"/votes/{guildId}";
        string target = returnUrl is not null && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith('/') ? returnUrl : fallback;
        string path = target.Split('#')[0];
        // Tab panels are named after the board: votes-<group>-<tab> on a group page, votes-server-<tab> on the Votes page.
        string anchor = path.StartsWith("/groups/", StringComparison.Ordinal)
            ? groupId is { } group ? $"votes-{group:N}-{tab}" : "votes"
            : $"votes-server-{tab}";
        return Redirect(path + "#" + anchor);
    }
}
