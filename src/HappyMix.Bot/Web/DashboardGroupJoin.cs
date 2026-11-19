using HappyMix.Application.Guilds;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>Open groups, join requests and game suggestions on the group page.</summary>
public sealed partial class DashboardController
{
    [HttpPost("/groups/{guildId}/{groupId:guid}/open")]
    public Task<IActionResult> SetGroupOpen(ulong guildId, Guid groupId, bool open) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new SetGroupOpenCommand(actor, group.Id, open),
            open ? "The group is open: anyone can ask to join" : "The group is invitation only", section: "settings");

    [HttpPost("/groups/{guildId}/{groupId:guid}/join")]
    public Task<IActionResult> RequestToJoinGroup(ulong guildId, Guid groupId) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new RequestToJoinGroupCommand(actor, group.Id),
            "Request sent. The organizer will get a message", section: "overview");

    [HttpPost("/groups/{guildId}/{groupId:guid}/join/cancel")]
    public Task<IActionResult> CancelJoinRequest(ulong guildId, Guid groupId) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new CancelJoinRequestCommand(actor, group.Id),
            "Request withdrawn", section: "overview");

    [HttpPost("/groups/{guildId}/{groupId:guid}/requests/{memberId}")]
    public Task<IActionResult> ResolveJoinRequest(ulong guildId, Guid groupId, ulong memberId, bool approve) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new ResolveJoinRequestCommand(actor, group.Id, memberId, approve),
            approve ? "Request approved: they're in the group" : "Request declined", section: "members");

    [HttpPost("/groups/{guildId}/{groupId:guid}/suggest")]
    public Task<IActionResult> SuggestGroupGame(ulong guildId, Guid groupId, string? name, string? steam, string? provider, string? externalId) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new SuggestGroupGameCommand(actor, group.Id, name ?? "", steam, provider, externalId),
            "Suggestion sent to the organizer", section: "games", form: new GroupFormState("suggest", null, name, steam));

    [HttpPost("/groups/{guildId}/{groupId:guid}/suggestions/{suggestionId:guid}")]
    public Task<IActionResult> ResolveGameSuggestion(ulong guildId, Guid groupId, Guid suggestionId, bool accept) =>
        RunGroupActionAsync(guildId, groupId, (actor, group) => new ResolveGameSuggestionCommand(actor, group.Id, suggestionId, accept),
            accept ? "Game added" : "Suggestion dismissed", section: "games");
}
