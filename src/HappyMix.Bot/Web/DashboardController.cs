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
    Microsoft.Extensions.Localization.IStringLocalizer<DashboardResources> text,
    IOptions<HappyMix.Application.Abstractions.Guilds.CommunityDefaults> community)
    : Controller
{
    private const string LastServerCookie = "hm_last_server";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>The sign-in page: "Keep me signed in" and the "Sign in with Discord" button.</summary>
    [AllowAnonymous]
    [HttpGet("/login")]
    public IActionResult Login(string? returnUrl, bool signedOut = false)
    {
        string back = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(back);
        return View("Login", new LoginPage(back, signedOut));
    }

    [AllowAnonymous]
    [HttpPost("/login")]
    public IActionResult LoginWithDiscord(string? returnUrl, bool remember = false) =>
        Challenge(new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/", IsPersistent = remember }, "Discord");

    /// <summary>
    /// "Sync with Discord": a quick trip through Discord (no prompt for people who already allowed HappyMix) that saves
    /// the latest name, avatar and Steam connection. "Keep me signed in" carries over.
    /// </summary>
    [HttpPost("/account/sync")]
    public async Task<IActionResult> SyncWithDiscord(string? returnUrl)
    {
        var current = await HttpContext.AuthenticateAsync();
        return Challenge(new AuthenticationProperties
        {
            RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/",
            IsPersistent = current.Properties?.IsPersistent == true
        }, "Discord");
    }

    [AllowAnonymous]
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
    [AllowAnonymous]
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

    // Side panel order without JavaScript: one move per click, or back to the default. dashboard.js reorders in place
    // (buttons and drag and drop) and writes the same cookie.
    [AllowAnonymous]
    [HttpPost("/nav-order")]
    public IActionResult SetNavOrder(string? move, bool reset, string? returnUrl)
    {
        var options = new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/" };
        if (reset)
            Response.Cookies.Delete(SideNavOrder.Cookie, new CookieOptions { Path = "/" });
        else if (move?.Split(':') is [var key, var direction] && direction is "up" or "down")
            Response.Cookies.Append(SideNavOrder.Cookie, string.Join(SideNavOrder.Separator, SideNavOrder.Move(SideNavOrder.Read(Request), key, direction == "up" ? -1 : 1)), options);
        else
            return BadRequest();
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    [HttpPost("/logout")]
    public IActionResult Logout() => SignOut(new AuthenticationProperties { RedirectUri = "/login?signedOut=true" });

    [AllowAnonymous]
    [HttpGet("/signed-out")]
    public IActionResult SignedOut() => LocalRedirect("/login?signedOut=true");

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
            ? new BannerModel(TempData["MessageKind"] as string ?? "success", title, TempData["Message"] as string)
            : null;
        return await RenderAvailabilityAsync(guildId, member, view is "weekly" or "members" ? view : "today",
            edit, saved, banner, new AvailabilityForm());
    }

    /// <summary>Adds do-not-disturb: weekly hours on chosen days, or hours (or whole days) on a run of dates.</summary>
    [HttpPost("/availability/{guildId}/dnd")]
    public async Task<IActionResult> AddDoNotDisturb(ulong guildId, string? view, string? zone, DayOfWeek[]? days, DateOnly? first, DateOnly? through,
        string? start, string? end, bool fullDay = false)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        bool weekly = view == "weekly";
        string timeZone = zone is { Length: > 0 } ? zone : (await GetGuildZoneAsync(guildId, HttpContext.RequestAborted)).Id;
        var result = weekly
            ? await sender.Send(new HappyMix.Application.Quiet.AddQuietWeeklyCommand(member.Id, days ?? [], start ?? "", end ?? "", fullDay, timeZone),
                HttpContext.RequestAborted)
            : first is { } from
                ? await sender.Send(new HappyMix.Application.Quiet.AddQuietDatesCommand(member.Id, from, through is { } to && to > from ? to : from,
                    start ?? "", end ?? "", fullDay, timeZone), HttpContext.RequestAborted)
                : HappyMix.Domain.Abstractions.Result.Failure<int>(new("Quiet.Invalid", "Choose a date."));
        return DoNotDisturbDone(guildId, weekly, result.IsSuccess ? text["Dnd_Saved"].Value : text["That_didn_t_work"].Value,
            result.IsFailure ? text.Message(result.Error.Description).Value : null, result.IsFailure);
    }

    [HttpPost("/availability/{guildId}/dnd/remove")]
    public async Task<IActionResult> RemoveDoNotDisturb(ulong guildId, Guid id, string? view)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        var result = await sender.Send(new HappyMix.Application.Quiet.RemoveQuietCommand(member.Id, id), HttpContext.RequestAborted);
        return DoNotDisturbDone(guildId, view == "weekly", result.IsSuccess ? text["Dnd_Removed"].Value : text["That_didn_t_work"].Value,
            result.IsFailure ? text.Message(result.Error.Description).Value : null, result.IsFailure);
    }

    private RedirectResult DoNotDisturbDone(ulong guildId, bool weekly, string title, string? body, bool failed)
    {
        TempData["MessageTitle"] = title;
        TempData["Message"] = body;
        TempData["MessageKind"] = failed ? "error" : "success";
        return Redirect($"/availability/{guildId}{(weekly ? "?view=weekly" : "")}#dnd");
    }

    /// <summary>
    /// One kind per weekly range: the editor posts a type (free time or do not disturb) and, for free time, a certainty.
    /// A do-not-disturb type wins; without types (older pages) the certainty list is used as it is.
    /// </summary>
    internal static string[] RangeKinds(string[]? types, string[]? kinds)
    {
        kinds ??= [];
        if (types is null || types.Length == 0)
            return kinds;
        return types.Select((type, index) => type == "dnd" ? "dnd" : index < kinds.Length && kinds[index] == "tentative" ? "tentative" : "definite").ToArray();
    }

    [HttpPost("/availability/{guildId}")]
    public async Task<IActionResult> Save(ulong guildId, string action, string? start, string? end,
        DayOfWeek? day, string? offset, Guid? windowId, string? startHour, string? startMinute,
        string? endHour, string? endMinute, bool allDay = false, bool tentative = false,
        string[]? rangeStartHour = null, string[]? rangeStartMinute = null, string[]? rangeEndHour = null,
        string[]? rangeEndMinute = null, string[]? rangeKind = null, string[]? rangeType = null, DayOfWeek[]? copyTo = null,
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
                Times(rangeEndHour, rangeEndMinute), RangeKinds(rangeType, rangeKind));

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
                // Clearing a day clears its do-not-disturb hours too: the week shows them as one day.
                bool hadQuiet = (await sender.Send(new HappyMix.Application.Quiet.GetQuietOverviewQuery(userId), cancellationToken)).Value
                    .Schedule.Weekly.Any(range => range.Day == clearDay);
                if (hadQuiet)
                    await sender.Send(new HappyMix.Application.Quiet.SetQuietDayCommand(userId, clearDay, [], await QuietZoneAsync(guildId, null)),
                        cancellationToken);
                var result = await sender.Send(new ClearWeeklyAvailabilityCommand(guildId, userId, clearDay), cancellationToken);
                if (result.IsFailure && !hadQuiet)
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

        var quiet = view == "members" ? null
            : (await sender.Send(new HappyMix.Application.Quiet.GetQuietOverviewQuery(member.Id), cancellationToken)).Value;
        var week = Enumerable.Range(1, 7).Select(index => (DayOfWeek)(index % 7)).Select(day =>
        {
            var windows = weekly.Where(item => item.Day == day).OrderBy(item => item.Start).ToArray();
            var quietHours = quiet?.Schedule.Weekly.Where(item => item.Day == day).OrderBy(item => item.Start).ToArray() ?? [];
            return new WeekRow(day, windows, day == now.DayOfWeek, view == "weekly" && edit == day,
                saved == day && (windows.Length > 0 || quietHours.Length > 0)) { Quiet = quietHours };
        }).ToArray();
        // The By date view shows the picked day: your free hours, do-not-disturb and events, in the server zone.
        HappyMix.Application.Guilds.BoardRow? myDay = view == "today"
            ? (await sender.Send(new GetMembersAvailabilityQuery(guildId, [member.Id], selectedDate, options.Value.TimeZoneId), cancellationToken))
                .Value.Rows.FirstOrDefault()
            : null;

        var guild = discord.GetGuild(guildId);
        var (invitations, accepted, _) = await LoadEventRowsAsync(guildId, member.Id);
        var eventBlocks = await LoadEventBlocksAsync(guildId, member.Id, zone, today);
        MembersBoardModel? membersBoard = null;
        if (view == "members")
        {
            var (profiles, available) = await memberDirectory.GetMembersAsync(guildId, cancellationToken);
            var ids = profiles.Values.Where(profile => !profile.IsBot && profile.InServer).Select(profile => profile.Id).ToArray();
            var data = (await sender.Send(new GetMembersAvailabilityQuery(guildId, ids, selectedDate, options.Value.TimeZoneId, IncludeMonth: true),
                cancellationToken)).Value;
            membersBoard = new MembersBoardModel(data, new MemberNames(profiles, available), member.Id,
                DayUrl: day => $"/availability/{guildId}?view=members&date={day:yyyy-MM-dd}",
                Events: await LoadBoardEventsAsync(guildId, member.Id, data.Rows.Select(row => row.UserId), data.Date, data.TimeZone));
        }
        return View("Availability", new AvailabilityPage(guildId, guild?.Name ?? "HappyMix",
            member.GuildPermissions.Administrator, view, zone.Id,
            AvailabilityFormat.Offset(zone.GetUtcOffset(clock.UtcNow)),
            selectedDate.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture), rows, week,
            (selectedDate == today ? GetPresets(now, weekly.FirstOrDefault(item => item.Day == now.DayOfWeek)) : []), banner, form)
        {
            MyEvents = accepted.Take(6).ToArray(), EventInvitations = invitations.Count,
            DoNotDisturb = quiet, MyDay = myDay,
            EventBlocks = eventBlocks, Members = membersBoard,
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
            string kind = index < kinds.Length ? kinds[index] : "definite";
            bool fullDay = start == "00:00" && end == "00:00";
            ranges.Add(new WeeklyRangeInput(start, end, fullDay, kind == "tentative", kind == "dnd"));
        }

        // Do-not-disturb ranges belong to the Discord account (every server), free hours to this server.
        var free = ranges.Where(range => !range.DoNotDisturb).ToArray();
        var quietRanges = ranges.Where(range => range.DoNotDisturb).ToArray();
        string dayName = CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(day);
        var result = await sender.Send(new SetWeeklyDayCommand(guildId, member.Id, day, free, offset, options.Value.TimeZoneId),
            HttpContext.RequestAborted);
        var quietResult = result.IsSuccess
            ? await sender.Send(new HappyMix.Application.Quiet.SetQuietDayCommand(member.Id, day, quietRanges,
                await QuietZoneAsync(guildId, offset)), HttpContext.RequestAborted)
            : null;
        if (result.IsFailure || quietResult is { IsFailure: true })
            return await RenderAvailabilityAsync(guildId, member, "weekly", day, null,
                Error(text["Couldn_t_save_Arg0", dayName], text.Message(result.IsFailure ? result.Error.Description : quietResult!.Error.Description)),
                new AvailabilityForm(Offset: offset) { Ranges = ranges });

        string name = CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(day);
        return ranges.Count == 0
            ? Saved(text["Arg0_cleared", name], null, guildId, "weekly", day, false)
            : Saved(text["Arg0_saved", name], text["Arg0_ranges", ranges.Count], guildId, "weekly", day);
    }

    /// <summary>The zone the weekly editor used: its own field (a zone name or a UTC offset), else the server's zone.</summary>
    private async Task<string> QuietZoneAsync(ulong guildId, string? offset)
    {
        if (offset?.Trim() is { Length: > 0 } value)
        {
            if (HappyMix.Application.Guilds.LocalTimes.FindZone(value) is { } named)
                return named.Id;
            if (HappyMix.Application.Guilds.LocalTimes.FindZone("UTC" + (value[0] is '+' or '-' ? value : "+" + value)) is { } fixedZone)
                return fixedZone.Id;
        }
        return (await GetGuildZoneAsync(guildId, HttpContext.RequestAborted)).Id;
    }

    private async Task<IActionResult> CopyWeeklyAsync(ulong guildId, Discord.Rest.RestGuildUser member, DayOfWeek from, DayOfWeek[] to)
    {
        var names = CultureInfo.CurrentUICulture.DateTimeFormat;
        // Do-not-disturb hours on the day are copied with it, in their own time zone.
        var quiet = (await sender.Send(new HappyMix.Application.Quiet.GetQuietOverviewQuery(member.Id), HttpContext.RequestAborted)).Value
            .Schedule.Weekly.Where(range => range.Day == from).ToArray();
        var result = await sender.Send(new CopyWeeklyDayCommand(guildId, member.Id, from, to), HttpContext.RequestAborted);
        // Like the free hours, the target days' do-not-disturb is replaced by the copied day's (none clears it).
        if (result.IsSuccess || quiet.Length > 0)
            foreach (var target in to.Distinct().Where(day => day != from && Enum.IsDefined(day)))
                await sender.Send(new HappyMix.Application.Quiet.SetQuietDayCommand(member.Id, target,
                    quiet.Select(range => new WeeklyRangeInput(AvailabilityFormat.Time(range.Start), AvailabilityFormat.Time(range.End), range.IsFullDay,
                        DoNotDisturb: true)).ToArray(), quiet.Length > 0 ? quiet[0].TimeZone : await QuietZoneAsync(guildId, null)), HttpContext.RequestAborted);
        if (result.IsFailure && quiet.Length == 0)
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

    /// <summary>Accepted, not canceled event dates from yesterday through six weeks ahead, split at local midnight.</summary>
    private async Task<IReadOnlyList<EventBlock>> LoadEventBlocksAsync(ulong guildId, ulong userId, TimeZoneInfo zone, DateOnly today)
    {
        var mine = (await sender.Send(new HappyMix.Application.Events.GetMemberEventsQuery(guildId, userId), HttpContext.RequestAborted)).Value;
        var blocks = new List<EventBlock>();
        foreach (var details in mine.Events)
        foreach (var occurrence in details.Occurrences)
        {
            if (occurrence.Status == HappyMix.Application.Abstractions.Events.OccurrenceStatus.Canceled ||
                !details.Responses.Any(item => item.UserId == mine.ViewerId && item.Date == occurrence.Date && item.Accepted) &&
                details.Event.OrganizerId != mine.ViewerId)
                continue;
            var start = TimeZoneInfo.ConvertTime(occurrence.Start, zone).DateTime;
            var end = TimeZoneInfo.ConvertTime(occurrence.End, zone).DateTime;
            string time = $"{start:HH:mm}–{end:HH:mm}";
            string url = $"/events/{guildId}/{details.Event.Id}?date={occurrence.Date:yyyy-MM-dd}";
            bool confirmed = occurrence.Status == HappyMix.Application.Abstractions.Events.OccurrenceStatus.Confirmed;
            for (var day = DateOnly.FromDateTime(start); day.ToDateTime(TimeOnly.MinValue) < end; day = day.AddDays(1))
            {
                if (day < today.AddDays(-1) || day > today.AddDays(42))
                    continue;
                var dayStart = day.ToDateTime(TimeOnly.MinValue);
                double from = Math.Max(0, (start - dayStart).TotalHours), until = Math.Min(24, (end - dayStart).TotalHours);
                if (until > from)
                    blocks.Add(new EventBlock(day, from, until, details.Event.Title, time, confirmed, url));
            }
        }
        return blocks.OrderBy(block => block.Date).ThenBy(block => block.StartHour).ToArray();
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
