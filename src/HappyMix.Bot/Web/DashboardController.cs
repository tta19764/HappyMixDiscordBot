using System.Security.Claims;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Infrastructure.PandaScore;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Web;

public sealed record DashboardServer(ulong Id, string Name, bool CanConfigure);

public sealed record DashboardPage(ulong GuildId, string GuildName,
    IReadOnlyList<AvailabilityWindow> Upcoming, IReadOnlyList<WeeklyAvailability> Weekly,
    string TimeZone, string? Message);

[Authorize]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class DashboardController(ISender sender, DiscordSocketClient discord, IOptions<PandaScoreOptions> options)
    : Controller
{
    [AllowAnonymous]
    [HttpGet("/login")]
    public IActionResult Login(string? returnUrl) =>
        Challenge(new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/" }, "Discord");

    [HttpPost("/logout")]
    public IActionResult Logout() => SignOut(new AuthenticationProperties { RedirectUri = "/signed-out" });

    [AllowAnonymous]
    [HttpGet("/signed-out")]
    public IActionResult SignedOut() => Content("Signed out of HappyMix.");

    [HttpGet("/")]
    public async Task<IActionResult> Index()
    {
        var servers = new List<DashboardServer>();
        foreach (var guild in discord.Guilds.OrderBy(guild => guild.Name))
        {
            if (await GetDiscordMemberAsync(guild.Id) is { } member)
                servers.Add(new DashboardServer(guild.Id, guild.Name, member.GuildPermissions.Administrator));
        }
        return View(servers.ToArray());
    }

    [HttpGet("/availability/{guildId}")]
    public async Task<IActionResult> Availability(ulong guildId)
    {
        var member = await GetMemberAsync(guildId);
        if (member is null)
            return StatusCode(StatusCodes.Status403Forbidden);

        return await RenderAsync(guildId, member.Value, TempData["Message"] as string);
    }

    [HttpPost("/availability/{guildId}")]
    public async Task<IActionResult> Save(ulong guildId, string action, string? start, string? end,
        DayOfWeek? day, string? offset, Guid? windowId)
    {
        var member = await GetMemberAsync(guildId);
        if (member is not { } userId)
            return StatusCode(StatusCodes.Status403Forbidden);

        CancellationToken cancellationToken = HttpContext.RequestAborted;
        offset = string.IsNullOrWhiteSpace(offset) ? null : offset;
        HappyMix.Domain.Abstractions.Result result;
        if (action == "clear" && day is { } clearDay)
            result = await sender.Send(new ClearWeeklyAvailabilityCommand(guildId, userId, clearDay), cancellationToken);
        else if (action == "remove" && windowId is { } id)
            result = await sender.Send(new RemoveAvailabilityCommand(guildId, userId, id), cancellationToken);
        else if (action == "today" && start is not null && end is not null)
            result = await sender.Send(new SetTodayAvailabilityCommand(guildId, userId, start, end,
                offset, options.Value.TimeZoneId), cancellationToken);
        else if (action == "weekly" && day is { } weekday && start is not null && end is not null)
            result = await sender.Send(new SetWeeklyAvailabilityCommand(guildId, userId, weekday, start, end,
                offset, options.Value.TimeZoneId), cancellationToken);
        else
            return BadRequest();

        if (result.IsFailure)
            return await RenderAsync(guildId, userId, result.Error.Description);

        TempData["Message"] = "Availability updated.";
        return RedirectToAction(nameof(Availability), new { guildId });
    }

    private async Task<IActionResult> RenderAsync(ulong guildId, ulong userId, string? message)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var upcoming = await sender.Send(new GetUpcomingAvailabilityQuery(guildId, userId), cancellationToken);
        var weekly = await sender.Send(new GetWeeklyAvailabilityQuery(guildId, userId), cancellationToken);
        var settings = await sender.Send(new GetGuildSettingsQuery(guildId), cancellationToken);
        return View("Availability", new DashboardPage(guildId, discord.GetGuild(guildId)?.Name ?? "HappyMix",
            upcoming.Value, weekly.Value, settings.Value?.TimeZoneId ?? options.Value.TimeZoneId, message));
    }

    private async Task<ulong?> GetMemberAsync(ulong guildId) => (await GetDiscordMemberAsync(guildId))?.Id;

    private async Task<Discord.Rest.RestGuildUser?> GetDiscordMemberAsync(ulong guildId)
    {
        if (!ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out ulong userId) ||
            discord.GetGuild(guildId) is null)
            return null;

        try
        {
            var guild = await discord.Rest.GetGuildAsync(guildId);
            return await guild.GetUserAsync(userId);
        }
        catch (HttpException exception) when (exception.DiscordCode == Discord.DiscordErrorCode.UnknownMember)
        {
            return null;
        }
    }
}
