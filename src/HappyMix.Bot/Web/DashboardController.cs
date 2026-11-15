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
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Web;

[Authorize]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class DashboardController(ISender sender, DiscordSocketClient discord,
    IOptions<PandaScoreOptions> options, IOptions<DiscordOptions> discordOptions, IClock clock,
    HappyMix.Bot.Services.IMemberDirectory memberDirectory,
    Microsoft.Extensions.Localization.IStringLocalizer<DashboardResources> text)
    : Controller
{
    private const string LastServerCookie = "hm_last_server";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [AllowAnonymous]
    [HttpGet("/login")]
    public IActionResult Login(string? returnUrl) =>
        Challenge(new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/" }, "Discord");

    [HttpPost("/language")]
    public IActionResult SetLanguage(string culture, string? returnUrl)
    {
        if (culture is not ("en" or "uk" or "ru"))
            return BadRequest();

        Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), Secure = Request.IsHttps,
                HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    // Fallback for browsers without JavaScript; dashboard.js switches the theme in place and writes the same cookie.
    [HttpPost("/theme")]
    public IActionResult SetTheme(string theme, string? returnUrl)
    {
        if (theme is not ("system" or "light" or "dark"))
            return BadRequest();

        if (theme == "system")
            Response.Cookies.Delete(DashboardTheme.Cookie, new CookieOptions { Path = "/" });
        else
            Response.Cookies.Append(DashboardTheme.Cookie, theme, new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1),
                Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/" });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

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
        DayOfWeek? day, string? offset, Guid? windowId, string? startHour, string? startMinute,
        string? endHour, string? endMinute, bool allDay = false, bool tentative = false,
        string[]? rangeStartHour = null, string[]? rangeStartMinute = null, string[]? rangeEndHour = null,
        string[]? rangeEndMinute = null, string[]? rangeKind = null, DayOfWeek[]? copyTo = null,
        string? fromDate = null, string? toDate = null)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        CancellationToken cancellationToken = HttpContext.RequestAborted;
        ulong userId = member.Id;
        offset = string.IsNullOrWhiteSpace(offset) ? null : offset.Trim();
        // The editor posts hour and minute selects; plain start/end values are still accepted.
        start ??= TimeRangeModel.Join(startHour, startMinute);
        end ??= TimeRangeModel.Join(endHour, endMinute);
        if (action == "today" && !string.IsNullOrWhiteSpace(Request.Form["date"]) &&
            !DateOnly.TryParseExact(Request.Form["date"], "yyyy-MM-dd", Culture, DateTimeStyles.None, out _))
            return BadRequest("Use a valid date in YYYY-MM-DD format.");
        var form = new AvailabilityForm(start, end, offset, allDay, tentative);
        bool hasTimes = allDay || (start is not null && end is not null);

        if (action == "weekly-day" && day is { } editedDay)
            return await SaveWeeklyDayAsync(guildId, member, editedDay, offset, Times(rangeStartHour, rangeStartMinute),
                Times(rangeEndHour, rangeEndMinute), rangeKind ?? []);

        if (action == "copy-weekly" && day is { } copyFrom)
            return await CopyWeeklyAsync(guildId, member, copyFrom, copyTo ?? []);
        if (action == "copy-date")
            return await CopyDateAsync(guildId, member, fromDate, toDate);

        switch (action)
        {
            case "today" when !hasTimes:
                return await RenderAvailabilityAsync(guildId, member, "today", null, null,
                    Error(text["Couldn_t_save_today"], text["Pick_a_start_and_end_time_or_choose_Free_all_day"]), form);
            case "weekly" when day is { } missingDay && !hasTimes:
                return await RenderAvailabilityAsync(guildId, member, "weekly", missingDay, null,
                    Error(text["Couldn_t_save_Arg0", missingDay], text["Pick_a_start_and_end_time_or_choose_Free_all_day"]), form);
            case "today":
            {
                var zone = await GetGuildZoneAsync(guildId, cancellationToken);
                var first = SelectedAvailabilityDate(zone);
                var lastText = Request.Form["through"].ToString();
                if (!DateOnly.TryParseExact(lastText, "yyyy-MM-dd", Culture, DateTimeStyles.None, out var last))
                {
                    if (!string.IsNullOrWhiteSpace(lastText))
                        return BadRequest("Use a valid end date in YYYY-MM-DD format.");
                    last = first;
                }
                var result = await sender.Send(new SetDateAvailabilityCommand(guildId, userId, first, last, start ?? "", end ?? "",
                    offset, options.Value.TimeZoneId, allDay, tentative), cancellationToken);
                if (result.IsFailure)
                    return await RenderAvailabilityAsync(guildId, member, "today", null, null,
                        Error("Couldn't save today", result.Error.Description), form);

                return Saved("Availability saved", text.Message(tentative ? "Tentative" : "Definite"), guildId, "today");
            }
            case "weekly" when day is { } weekday:
            {
                var result = await sender.Send(new SetWeeklyAvailabilityCommand(guildId, userId, weekday, start ?? "", end ?? "",
                    offset, options.Value.TimeZoneId, allDay, tentative), cancellationToken);
                if (result.IsFailure)
                    return await RenderAvailabilityAsync(guildId, member, "weekly", weekday, null,
                        Error($"Couldn't save {weekday}", result.Error.Description), form);

                return Saved("Availability saved", text.Message(tentative ? "Tentative" : "Definite"), guildId, "weekly", weekday);
            }
            case "remove-weekly" when day is { } rangeDay && TimeOnly.TryParse(start, CultureInfo.InvariantCulture, out var removeStart) && offset is not null:
            {
                var result = await sender.Send(new ClearWeeklyAvailabilityCommand(guildId, userId, rangeDay, removeStart, offset), cancellationToken);
                if (result.IsFailure)
                    return await RenderAvailabilityAsync(guildId, member, "weekly", null, null,
                        Error("Couldn't remove that window", result.Error.Description), new AvailabilityForm());
                return Saved("Window removed", null, guildId, "weekly", rangeDay);
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
            new { guildId, view = view == "weekly" ? "weekly" : null, saved = highlight ? day : null, date = Request.HasFormContentType ? Request.Form["date"].ToString() : Request.Query["date"].ToString() },
            day?.ToString().ToLowerInvariant());
    }

    /// <summary>A range in the server zone, shown as "Today · All day" when it covers a whole local day.</summary>
    private string RangeText(DateTime start, DateTime end, DateOnly today) => AvailabilityFormat.IsFullDay(start, end)
        ? $"{AvailabilityFormat.Day(DateOnly.FromDateTime(start), today)} · {text["All_day"]}"
        : AvailabilityFormat.Range(start, end, today);

    private static BannerModel Error(string title, string text) => new("error", title, text);

    private async Task<IActionResult> RenderAvailabilityAsync(ulong guildId, Discord.Rest.RestGuildUser member,
        string view, DayOfWeek? edit, DayOfWeek? saved, BannerModel? banner, AvailabilityForm form)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var zone = await GetGuildZoneAsync(guildId, cancellationToken);
        var now = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        var today = DateOnly.FromDateTime(now.DateTime);
        var selectedDate = SelectedAvailabilityDate(zone);
        var upcoming = (await sender.Send(new GetUpcomingAvailabilityQuery(guildId, member.Id, IncludePast: true), cancellationToken)).Value;
        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(guildId, member.Id), cancellationToken)).Value;

        var rows = upcoming.Where(window => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.Start, zone).DateTime) <= selectedDate &&
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.End.AddTicks(-1), zone).DateTime) >= selectedDate)
            .OrderBy(window => window.Start).Select(window =>
        {
            var start = TimeZoneInfo.ConvertTime(window.Start, zone).DateTime;
            var end = TimeZoneInfo.ConvertTime(window.End, zone).DateTime;
            string meta = start.ToString("ddd d MMM", CultureInfo.CurrentCulture) + (end.Date > start.Date ? " · Overnight" : "");
            return new UpcomingRow(window.Id, RangeText(start, end, today), meta, window.Tentative);
        }).ToArray();

        var week = Enumerable.Range(1, 7).Select(index => (DayOfWeek)(index % 7)).Select(day =>
        {
            var windows = weekly.Where(item => item.Day == day).OrderBy(item => item.Start).ToArray();
            return new WeekRow(day, windows, day == now.DayOfWeek, view == "weekly" && edit == day,
                saved == day && windows.Length > 0);
        }).ToArray();

        var guild = discord.GetGuild(guildId);
        var (invitations, accepted, _) = await LoadEventRowsAsync(guildId, member.Id);
        return View("Availability", new AvailabilityPage(guildId, guild?.Name ?? "HappyMix",
            member.GuildPermissions.Administrator, view, zone.Id,
            AvailabilityFormat.Offset(zone.GetUtcOffset(clock.UtcNow)),
            selectedDate.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture), rows, week,
            (selectedDate == today ? GetPresets(now, weekly.FirstOrDefault(item => item.Day == now.DayOfWeek)) : []), banner, form)
        {
            MyEvents = accepted.Take(6).ToArray(), EventInvitations = invitations.Count,
            SelectedDate = selectedDate,
            ConfiguredDates = upcoming.SelectMany(window =>
            {
                var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.Start, zone).DateTime);
                var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.End.AddTicks(-1), zone).DateTime);
                return Enumerable.Range(first.DayNumber, last.DayNumber - first.DayNumber + 1).Select(DateOnly.FromDayNumber);
            }).ToHashSet()
        });
    }

    /// <summary>
    /// Saves the day editor: every range on that day (in the editor's time zone) is replaced at once, so existing
    /// ranges can be moved, resized, retyped or removed. Blank rows are ignored; 00:00–00:00 means all day.
    /// </summary>
    // Each range row posts an hour and a minute select; an empty hour means that time was left empty.
    private static string[] Times(string[]? hours, string[]? minutes) => (hours ?? []).Select((hour, index) =>
        string.IsNullOrWhiteSpace(hour) ? "" : $"{hour.Trim()}:{(minutes is not null && index < minutes.Length && minutes[index] is { Length: > 0 } minute ? minute.Trim() : "00")}")
        .ToArray();

    private async Task<IActionResult> SaveWeeklyDayAsync(ulong guildId, Discord.Rest.RestGuildUser member, DayOfWeek day,
        string? offset, string[] starts, string[] ends, string[] kinds)
    {
        var ranges = new List<WeeklyRangeInput>();
        for (int index = 0; index < Math.Max(starts.Length, ends.Length); index++)
        {
            string start = index < starts.Length ? starts[index]?.Trim() ?? "" : "";
            string end = index < ends.Length ? ends[index]?.Trim() ?? "" : "";
            if (start.Length == 0 && end.Length == 0)
                continue;
            bool tentative = index < kinds.Length && kinds[index] == "tentative";
            bool fullDay = start == "00:00" && end == "00:00";
            ranges.Add(new WeeklyRangeInput(start, end, fullDay, tentative));
        }

        var result = await sender.Send(new SetWeeklyDayCommand(guildId, member.Id, day, ranges, offset, options.Value.TimeZoneId),
            HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderAvailabilityAsync(guildId, member, "weekly", day, null,
                Error(text["Couldn_t_save_Arg0", CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(day)], text.Message(result.Error.Description)),
                new AvailabilityForm(Offset: offset) { Ranges = ranges });

        string name = CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(day);
        return ranges.Count == 0
            ? Saved(text["Arg0_cleared", name], null, guildId, "weekly", day, false)
            : Saved(text["Arg0_saved", name], text["Arg0_ranges", ranges.Count], guildId, "weekly", day);
    }

    private async Task<IActionResult> CopyWeeklyAsync(ulong guildId, Discord.Rest.RestGuildUser member, DayOfWeek from, DayOfWeek[] to)
    {
        var names = CultureInfo.CurrentUICulture.DateTimeFormat;
        var result = await sender.Send(new CopyWeeklyDayCommand(guildId, member.Id, from, to), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderAvailabilityAsync(guildId, member, "weekly", null, null,
                Error(text["Couldn_t_copy_Arg0", names.GetDayName(from)], text.Message(result.Error.Description)), new AvailabilityForm());
        var targets = to.Distinct().Where(day => day != from).OrderBy(day => ((int)day + 6) % 7).Select(names.GetDayName).ToArray();
        return Saved(text["Copied_Arg0", names.GetDayName(from)], text["Now_also_on_Arg0", string.Join(", ", targets)],
            guildId, "weekly", to.Length == 1 ? to[0] : null);
    }

    private async Task<IActionResult> CopyDateAsync(ulong guildId, Discord.Rest.RestGuildUser member, string? fromText, string? toText)
    {
        if (!DateOnly.TryParseExact(fromText, "yyyy-MM-dd", Culture, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(toText, "yyyy-MM-dd", Culture, DateTimeStyles.None, out var to))
            return BadRequest("Use valid dates in YYYY-MM-DD format.");
        var result = await sender.Send(new CopyDateAvailabilityCommand(guildId, member.Id, from, to, options.Value.TimeZoneId),
            HttpContext.RequestAborted);
        var culture = CultureInfo.CurrentUICulture;
        if (result.IsFailure)
            return await RenderAvailabilityAsync(guildId, member, "today", null, null,
                Error(text["Couldn_t_copy_Arg0", from.ToString("d MMMM", culture)], text.Message(result.Error.Description)), new AvailabilityForm());
        return Saved(text["Copied_Arg0", from.ToString("d MMMM", culture)], text["Now_also_on_Arg0", to.ToString("dddd d MMMM", culture)], guildId, "today");
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
                End: TimeZoneInfo.ConvertTime(window.End, zone).DateTime, window.Tentative))
            .Where(window => DateOnly.FromDateTime(window.Start) <= today)
            .OrderBy(window => window.Start)
            .ToArray();
        var status = upcoming.Select(window => RangeText(window.Start, window.End, today) + " · " +
            text.Message(window.Tentative ? "Tentative" : "Definite")).ToList();

        var weekly = (await sender.Send(new GetWeeklyAvailabilityQuery(guildId, userId), cancellationToken)).Value;
        var ranges = weekly.SelectMany(window => WeeklyStatusRanges(window, today, zone))
            .Where(window => window.End > now && DateOnly.FromDateTime(window.Start.DateTime) <= today).ToArray();
        status.AddRange(ranges.Select(window =>
            RangeText(window.Start.DateTime, window.End.DateTime, today) + " · " + text.Message(window.Tentative ? "Tentative" : "Definite") + " · " + text["Weekly"]));
        return status.Count == 0 ? null : string.Join(" · ", status);
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End, bool Tentative)> WeeklyStatusRanges(
        WeeklyAvailability window, DateOnly today, TimeZoneInfo zone)
    {
        foreach (var range in WeeklyOccurrences.ForDate(window, today))
            yield return (TimeZoneInfo.ConvertTime(range.Start, zone), TimeZoneInfo.ConvertTime(range.End, zone), window.Tentative);
    }

    private DateOnly SelectedAvailabilityDate(TimeZoneInfo zone)
    {
        string value = Request.HasFormContentType ? Request.Form["date"].ToString() : Request.Query["date"].ToString();
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", Culture, DateTimeStyles.None, out var date)
            ? date : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
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
