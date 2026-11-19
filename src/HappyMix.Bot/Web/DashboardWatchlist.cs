using HappyMix.Application.Abstractions.Media;
using HappyMix.Application.Abstractions.Watch;
using HappyMix.Application.Guilds;
using HappyMix.Application.Watch;
using HappyMix.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

/// <summary>A group's watchlist section.</summary>
public sealed record WatchModel(ulong GuildId, Guid GroupId, WatchBoard Board, string Language, string? Error)
{
    public string Url => $"/groups/{GuildId}/{GroupId}/watch";
    public string ItemUrl(WatchItem item) => $"{Url}/{item.Id}";
}

public sealed partial class DashboardController
{
    /// <summary>Catalog search for the vote and watchlist forms. Members only; queries go to the public catalogs.</summary>
    [HttpGet("/media/{guildId}/search")]
    public async Task<IActionResult> SearchMedia(ulong guildId, string? kind, string? q, [FromServices] IMediaCatalog catalog)
    {
        if (await GetDiscordMemberAsync(guildId) is null)
            return StatusCode(StatusCodes.Status403Forbidden);
        string language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var results = await catalog.SearchAsync(kind ?? "", q ?? "", HttpContext.RequestAborted);
        return Json(results.Select(item => new
        {
            kind = item.Kind, source = item.Source, id = item.ExternalId, title = item.Title, url = item.Url, image = item.ImageUrl,
            details = PollMessages.Details(item, language),
            steam = item.SteamAppId
        }));
    }

    [HttpPost("/groups/{guildId}/{groupId:guid}/watch")]
    public async Task<IActionResult> AddWatchItem(ulong guildId, Guid groupId, string? kind, string? title, string? url, string? source, string? externalId)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        MediaRef? reference = source is { Length: > 0 } && externalId is { Length: > 0 } ? new MediaRef(source, externalId) : null;
        var result = await sender.Send(new AddWatchItemCommand(actor, groupId, kind ?? "", reference, url, title), HttpContext.RequestAborted);
        return WatchRedirect(guildId, groupId, result.IsSuccess ? text["Watch_Added", result.Value.Title].Value : null,
            result.IsFailure ? text.Message(result.Error.Description).Value : null);
    }

    [HttpPost("/groups/{guildId}/{groupId:guid}/watch/{itemId:guid}")]
    public async Task<IActionResult> UpdateWatchItem(ulong guildId, Guid groupId, Guid itemId, string? action, WatchStatus? status,
        int? season, int? episode, string? position)
    {
        if (await GetGroupActorAsync(guildId) is not ({ } actor, _, _))
            return NotMember();
        var command = action switch
        {
            "next" => new UpdateWatchItemCommand(actor, groupId, itemId, NextEpisode: true),
            "finish" => new UpdateWatchItemCommand(actor, groupId, itemId, Finish: true),
            "status" => new UpdateWatchItemCommand(actor, groupId, itemId, Status: status),
            "progress" => new UpdateWatchItemCommand(actor, groupId, itemId, Season: season, Episode: episode, Position: position ?? ""),
            "remove" => null,
            _ => new UpdateWatchItemCommand(actor, groupId, itemId)
        };
        if (command is null)
        {
            var removed = await sender.Send(new RemoveWatchItemCommand(actor, groupId, itemId), HttpContext.RequestAborted);
            return WatchRedirect(guildId, groupId, removed.IsSuccess ? text["Watch_Removed"].Value : null,
                removed.IsFailure ? text.Message(removed.Error.Description).Value : null);
        }
        var result = await sender.Send(command, HttpContext.RequestAborted);
        return WatchRedirect(guildId, groupId, result.IsSuccess ? text["Watch_Saved"].Value : null,
            result.IsFailure ? text.Message(result.Error.Description).Value : null);
    }

    private async Task<WatchModel?> LoadWatchlistAsync(ulong guildId, GroupActor actor, Guid groupId)
    {
        var board = await sender.Send(new GetWatchlistQuery(actor, groupId), HttpContext.RequestAborted);
        return board.IsFailure ? null : new WatchModel(guildId, groupId, board.Value,
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, TempData["WatchError"] as string);
    }

    private RedirectResult WatchRedirect(ulong guildId, Guid groupId, string? success, string? error)
    {
        if (success is not null)
            TempData["GroupBannerTitle"] = success;
        if (error is not null)
            TempData["WatchError"] = error;
        return Redirect($"/groups/{guildId}/{groupId}#watchlist");
    }
}
