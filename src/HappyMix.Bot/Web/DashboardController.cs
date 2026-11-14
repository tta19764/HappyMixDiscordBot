using System.Globalization;
using System.Security.Claims;
using Discord.Net;
using Discord.WebSocket;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Bot.Configuration;
using HappyMix.Infrastructure.PandaScore;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Web;

[Authorize]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class DashboardController(ISender sender, DiscordSocketClient discord,
    IOptions<PandaScoreOptions> options, IOptions<DiscordOptions> discordOptions, IClock clock)
    : Controller
{
    private const string LastServerCookie = "hm_last_server";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

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
        ulong.TryParse(Request.Cookies[LastServerCookie], NumberStyles.None, Culture, out ulong lastUsed);
        var servers = new List<DashboardServer>();
        foreach (var guild in discord.Guilds.OrderBy(guild => guild.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (await GetDiscordMemberAsync(guild.Id) is { } member)
                servers.Add(new DashboardServer(guild.Id, guild.Name, guild.IconUrl,
                    member.GuildPermissions.Administrator, await GetTodayStatusAsync(guild.Id, member.Id),
                    guild.Id == lastUsed));
        }
        return View(servers.OrderByDescending(server => server.LastUsed).ToArray());
    }

    [HttpGet("/availability/{guildId}")]
    public async Task<IActionResult> Availability(ulong guildId, string? view, DayOfWeek? edit, DayOfWeek? saved)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        RememberServer(guildId);
        var banner = TempData["MessageTitle"] is string title
            ? new BannerModel("success", title, TempData["Message"] as string)
            : null;
        return await RenderAvailabilityAsync(guildId, member, view == "weekly" ? "weekly" : "today",
            edit, saved, banner, new AvailabilityForm());
    }

    [HttpPost("/availability/{guildId}")]
    public async Task<IActionResult> Save(ulong guildId, string action, string? start, string? end,
        DayOfWeek? day, string? offset, Guid? windowId)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        CancellationToken cancellationToken = HttpContext.RequestAborted;
        ulong userId = member.Id;
        offset = string.IsNullOrWhiteSpace(offset) ? null : offset.Trim();
        var form = new AvailabilityForm(start, end, offset);

        switch (action)
        {
            case "today" when start is not null && end is not null:
            {
                var result = await sender.Send(new SetTodayAvailabilityCommand(guildId, userId, start, end,
                    offset, options.Value.TimeZoneId), cancellationToken);
                if (result.IsFailure)
                    return await RenderAvailabilityAsync(guildId, member, "today", null, null,
                        Error("Couldn't save today", result.Error.Description), form);

                var window = result.Value;
                var date = DateOnly.FromDateTime(window.Start.DateTime);
                return Saved("Availability saved",
                    $"You're available {AvailabilityFormat.Range(window.Start.DateTime, window.End.DateTime, date)}.",
                    guildId, "today");
            }
            case "weekly" when day is { } weekday && start is not null && end is not null:
            {
                var result = await sender.Send(new SetWeeklyAvailabilityCommand(guildId, userId, weekday, start, end,
                    offset, options.Value.TimeZoneId), cancellationToken);
                if (result.IsFailure)
                    return await RenderAvailabilityAsync(guildId, member, "weekly", weekday, null,
                        Error($"Couldn't save {weekday}", result.Error.Description), form);

                var window = result.Value;
                string next = window.End < window.Start ? $" (into {AvailabilityFormat.ShortDay((DayOfWeek)(((int)weekday + 1) % 7))})" : "";
                return Saved($"{weekday} saved",
                    $"{AvailabilityFormat.Time(window.Start)} → {AvailabilityFormat.Time(window.End)}{next}, every {weekday}.",
                    guildId, "weekly", weekday);
            }
            case "clear" when day is { } clearDay:
            {
                var result = await sender.Send(new ClearWeeklyAvailabilityCommand(guildId, userId, clearDay), cancellationToken);
                if (result.IsFailure)
                    return await RenderAvailabilityAsync(guildId, member, "weekly", null, null,
                        Error($"Couldn't clear {clearDay}", result.Error.Description), new AvailabilityForm());

                return Saved($"{clearDay} cleared", $"You have no weekly hours on {clearDay} now.", guildId, "weekly", clearDay, false);
            }
            case "remove" when windowId is { } id:
            {
                var result = await sender.Send(new RemoveAvailabilityCommand(guildId, userId, id), cancellationToken);
                if (result.IsFailure)
                    return await RenderAvailabilityAsync(guildId, member, "today", null, null,
                        Error("Couldn't remove that window", result.Error.Description), new AvailabilityForm());

                return Saved("Window removed", null, guildId, "today");
            }
            default:
                return BadRequest();
        }
    }

    private RedirectToActionResult Saved(string title, string? text, ulong guildId, string view,
        DayOfWeek? day = null, bool highlight = true)
    {
        TempData["MessageTitle"] = title;
        TempData["Message"] = text;
        return RedirectToAction(nameof(Availability), null,
            new { guildId, view = view == "weekly" ? "weekly" : null, saved = highlight ? day : null },
            day?.ToString().ToLowerInvariant());
    }

    private static BannerModel Error(string title, string text) => new("error", title, text);

    private async Task<IActionResult> RenderAvailabilityAsync(ulong guildId, Discord.Rest.RestGuildUser member,
        string view, DayOfWeek? edit, DayOfWeek? saved, BannerModel? banner, AvailabilityForm form)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var zone = await GetGuildZoneAsync(guildId, cancellationToken);
        var now = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        var today = DateOnly.FromDateTime(now.DateTime);
        var upcoming = (await sender.Send(new GetUpcomingAvailabilityQuery(guildId, member.Id), cancellationToken)).Value;
        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(guildId, member.Id), cancellationToken)).Value;

        var rows = upcoming.OrderBy(window => window.Start).Select(window =>
        {
            var start = TimeZoneInfo.ConvertTime(window.Start, zone).DateTime;
            var end = TimeZoneInfo.ConvertTime(window.End, zone).DateTime;
            string meta = start.ToString("ddd d MMM", Culture) + (end.Date > start.Date ? " · Overnight" : "");
            return new UpcomingRow(window.Id, AvailabilityFormat.Range(start, end, today), meta);
        }).ToArray();

        var week = Enumerable.Range(1, 7).Select(index => (DayOfWeek)(index % 7)).Select(day =>
        {
            var window = weekly.FirstOrDefault(item => item.Day == day);
            var previous = weekly.FirstOrDefault(item => item.Day == (DayOfWeek)(((int)day + 6) % 7));
            double? carry = previous is { } p && p.End < p.Start ? AvailabilityFormat.Percent(p.End) : null;
            string? zoneNote = window is not null && window.TimeZone != zone.Id ? window.TimeZone : null;
            return new WeekRow(day, window, day == now.DayOfWeek, view == "weekly" && edit == day,
                saved == day && window is not null, carry, zoneNote);
        }).ToArray();

        if (view == "weekly" && edit is { } editing && form.Start is null &&
            week.First(row => row.Day == editing).Window is { } current)
            form = new AvailabilityForm(AvailabilityFormat.Time(current.Start), AvailabilityFormat.Time(current.End),
                current.TimeZone == zone.Id ? null : current.TimeZone);

        var guild = discord.GetGuild(guildId);
        return View("Availability", new AvailabilityPage(guildId, guild?.Name ?? "HappyMix",
            member.GuildPermissions.Administrator, view, zone.Id,
            AvailabilityFormat.Offset(zone.GetUtcOffset(clock.UtcNow)),
            now.ToString("dddd d MMMM", Culture), rows, week,
            GetPresets(now, weekly.FirstOrDefault(item => item.Day == now.DayOfWeek)), banner, form));
    }

    private static AvailabilityPreset[] GetPresets(DateTimeOffset now, WeeklyAvailability? todayWeekly)
    {
        var presets = new List<AvailabilityPreset>();
        var minute = now.Minute % 15 == 0 && now.Second == 0 ? now : now.AddMinutes(15 - now.Minute % 15);
        var next = TimeOnly.FromDateTime(minute.DateTime);
        presets.Add(new("Next 3 hours", next.ToString("HH:mm", Culture), next.AddHours(3).ToString("HH:mm", Culture)));
        var time = TimeOnly.FromDateTime(now.DateTime);
        if (time < new TimeOnly(23, 0))
            presets.Add(new("Evening", "19:00", "23:00"));
        presets.Add(new("Late night", "21:00", "02:00"));
        if (todayWeekly is not null && (todayWeekly.End < todayWeekly.Start || time < todayWeekly.End))
            presets.Add(new($"My {now.DayOfWeek} hours", AvailabilityFormat.Time(todayWeekly.Start),
                AvailabilityFormat.Time(todayWeekly.End)));
        return presets.ToArray();
    }

    private async Task<string?> GetTodayStatusAsync(ulong guildId, ulong userId)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var zone = await GetGuildZoneAsync(guildId, cancellationToken);
        var now = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        var today = DateOnly.FromDateTime(now.DateTime);
        var upcoming = (await sender.Send(new GetUpcomingAvailabilityQuery(guildId, userId), cancellationToken)).Value
            .Select(window => (Start: TimeZoneInfo.ConvertTime(window.Start, zone).DateTime,
                End: TimeZoneInfo.ConvertTime(window.End, zone).DateTime))
            .Where(window => DateOnly.FromDateTime(window.Start) <= today)
            .OrderBy(window => window.Start)
            .ToArray();
        if (upcoming.Length > 0)
            return AvailabilityFormat.Range(upcoming[0].Start, upcoming[0].End, today);

        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(guildId, userId), cancellationToken)).Value
            .FirstOrDefault(window => window.Day == now.DayOfWeek);
        if (weekly is null)
            return null;

        var end = (weekly.End < weekly.Start ? today.AddDays(1) : today).ToDateTime(weekly.End);
        return AvailabilityFormat.Range(today.ToDateTime(weekly.Start), end, today) + " · weekly";
    }

    private async Task<TimeZoneInfo> GetGuildZoneAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var settings = await sender.Send(new GetGuildSettingsQuery(guildId), cancellationToken);
        return TimeZoneInfo.TryFindSystemTimeZoneById(settings.Value?.TimeZoneId ?? options.Value.TimeZoneId, out var zone)
            ? zone : TimeZoneInfo.Utc;
    }

    private void RememberServer(ulong guildId) =>
        Response.Cookies.Append(LastServerCookie, guildId.ToString(Culture), new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = TimeSpan.FromDays(180)
        });

    private ViewResult NotMember() => Forbidden(new ForbiddenPage("Server not available",
        "You and HappyMix both need to be members of this server to manage it here.", "/", "Back to servers"));

    private ViewResult Forbidden(ForbiddenPage page)
    {
        var result = View("Forbidden", page);
        result.StatusCode = StatusCodes.Status403Forbidden;
        return result;
    }

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
