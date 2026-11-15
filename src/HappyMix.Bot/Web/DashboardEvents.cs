using System.Globalization;
using HappyMix.Application.Abstractions.Events;
using HappyMix.Application.Events;
using HappyMix.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace HappyMix.Bot.Web;

public sealed partial class DashboardController
{
    private const int InvitationDays = 14;

    [HttpGet("/events/{guildId}")]
    public async Task<IActionResult> Events(ulong guildId)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        RememberServer(guildId);
        var (invitations, accepted, organizing) = await LoadEventRowsAsync(guildId, member.Id);
        return View("Events", new EventsPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator, invitations, accepted,
            organizing, TakeEventBanner()));
    }

    [HttpGet("/events/{guildId}/new")]
    public async Task<IActionResult> NewEvent(ulong guildId, Guid? group)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        var zone = await GetGuildZoneAsync(guildId, HttpContext.RequestAborted);
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime).AddDays(1);
        var form = new EventForm { GroupId = group, StartDate = tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Weekdays = [tomorrow.DayOfWeek] };
        return await RenderEventFormAsync(guildId, member.Id, member.GuildPermissions.Administrator, form, null);
    }

    [HttpPost("/events/{guildId}/new")]
    public async Task<IActionResult> CreateEvent(ulong guildId, EventForm form)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        bool admin = member.GuildPermissions.Administrator;
        if (!DateOnly.TryParseExact(form.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
            return await RenderEventFormAsync(guildId, member.Id, admin, form, text["Pick_the_date_of_the_first_event"]);
        DateOnly? until = DateOnly.TryParseExact(form.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last) ? last : null;
        var recurrence = form.Repeat switch { "daily" => EventRecurrence.Daily, "weekly" => EventRecurrence.Weekly, _ => EventRecurrence.Once };
        bool forGroup = form.Audience == "group";
        if (forGroup && form.GroupId is null)
            return await RenderEventFormAsync(guildId, member.Id, admin, form, text["Choose_a_group_or_pick_people_instead"]);

        ulong[] invitees = [];
        if (!forGroup)
        {
            // Only current human members of this server can be invited.
            var (members, available) = await memberDirectory.GetMembersAsync(guildId);
            if (!available)
                return await RenderEventFormAsync(guildId, member.Id, admin, form, text["Discord_didn_t_return_the_member_list_Try_again_in_a_moment"]);
            invitees = form.Invitees.Where(id => members.TryGetValue(id, out var profile) && profile.InServer && !profile.IsBot).Distinct().ToArray();
        }

        var result = await sender.Send(new CreateEventCommand(guildId, member.Id, form.RequestId, forGroup ? form.GroupId : null, invitees,
            form.Title ?? "", form.Description, startDate, $"{form.StartHour}:{form.StartMinute}", $"{form.EndHour}:{form.EndMinute}",
            form.TimeZone, options.Value.TimeZoneId, recurrence, form.Weekdays, until), HttpContext.RequestAborted);
        if (result.IsFailure)
            return await RenderEventFormAsync(guildId, member.Id, admin, form, text.Message(result.Error.Description));

        TempData["EventBannerTitle"] = text["Event_created"].Value;
        TempData["EventBannerText"] = text["Arg0_invited_Invitations_go_out_by_Discord_message_a_few_days_before", result.Value.Invited].Value;
        return Redirect($"/events/{guildId}/{result.Value.EventId}");
    }

    [HttpGet("/events/{guildId}/{eventId:guid}")]
    public async Task<IActionResult> EventDetail(ulong guildId, Guid eventId, string? date)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();

        var result = await sender.Send(new GetEventQuery(guildId, member.Id, eventId), HttpContext.RequestAborted);
        if (result.IsFailure)
            return EventNotFound(guildId);

        var view = result.Value;
        var details = view.Details;
        if (details.Occurrences.Count == 0)
            return EventNotFound(guildId);
        var now = clock.UtcNow;
        var occurrence = (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked)
                ? details.Occurrences.FirstOrDefault(item => item.Date == picked) : null)
            ?? details.Occurrences.FirstOrDefault(item => item.End > now && item.Status != OccurrenceStatus.Canceled)
            ?? details.Occurrences.FirstOrDefault(item => item.End > now) ?? details.Occurrences[^1];

        var zone = EventText.Zone(details.Event);
        var (members, available) = await memberDirectory.GetMembersAsync(guildId);
        var names = new MemberNames(members, available);
        string Name(ulong id) => names.Name(id, key => text.Message(key));
        string? groupName = await GroupNameAsync(guildId, member.Id, details.Event.GroupId);

        var answers = details.Responses.Where(item => item.Date == occurrence.Date).ToDictionary(item => item.UserId);
        var failed = details.Deliveries.Where(item => item.Date == occurrence.Date && item.Kind == EventNotificationKind.Invite && item.Failed)
            .Select(item => item.UserId).ToHashSet();
        var responses = details.Invitees.Select(id =>
        {
            answers.TryGetValue(id, out var answer);
            return new EventResponseRow(new EventPerson(id, Name(id), names.Get(id)),
                answer is null ? "waiting" : answer.Accepted ? "accepted" : "declined", answer?.Withdrawn == true, failed.Contains(id), answer?.At);
        }).OrderBy(row => row.State switch { "accepted" => 0, "declined" => 1, _ => 2 }).ThenBy(row => row.Person.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var withdrawals = responses.Where(row => row.Withdrawn && occurrence.ConfirmedAt is { } confirmed && row.At > confirmed)
            .Select(row => row.Person.Name).ToArray();
        var dates = details.Occurrences.Where(item => item.End > now || item.Date == occurrence.Date).Take(40)
            .Select(item => EventText.Row(details, item, view.ViewerId, zone, groupName, Name, guildId)).ToArray();
        answers.TryGetValue(view.ViewerId, out var mine);

        return View("EventDetail", new EventDetailPage(guildId, GuildName(guildId), member.GuildPermissions.Administrator, view, occurrence,
            details.Event.Title, EventText.When(occurrence, zone), EventText.Schedule(details.Event, text), Name(details.Event.OrganizerId),
            groupName, details.Event.GroupId, EventRules.Count(details, occurrence.Date), responses, dates, mine?.Accepted, mine?.Withdrawn == true,
            occurrence.Start <= now, withdrawals, view.IsOrganizer ? failed.Count : 0, TakeEventBanner()));
    }

    [HttpPost("/events/{guildId}/{eventId:guid}/respond")]
    public Task<IActionResult> RespondToEvent(ulong guildId, Guid eventId, string date, string answer) =>
        RunEventActionAsync(guildId, eventId, date, (userId, day) => sender.Send(new RespondToEventCommand(guildId, userId, eventId, day, answer == "accept")),
            value => (value.Accepted ? text["You_re_going"] : value.Withdrawn ? text["You_withdrew"] : text["You_declined"]).Value,
            value => value.Withdrawn ? text["The_organizer_can_see_that_you_can_t_make_it"].Value : null);

    [HttpPost("/events/{guildId}/{eventId:guid}/confirm")]
    public Task<IActionResult> ConfirmEvent(ulong guildId, Guid eventId, string date) =>
        RunEventActionAsync(guildId, eventId, date, (userId, day) => sender.Send(new ConfirmEventCommand(guildId, userId, eventId, day)),
            _ => text["Date_confirmed"].Value, value => text["Arg0_going_Everyone_who_accepted_gets_a_Discord_message", value.Accepted].Value);

    [HttpPost("/events/{guildId}/{eventId:guid}/cancel")]
    public Task<IActionResult> CancelEvent(ulong guildId, Guid eventId, string date, string scope) =>
        RunEventActionAsync(guildId, eventId, date, (userId, day) => sender.Send(new CancelEventCommand(guildId, userId, eventId, scope == "series" ? null : day)),
            count => (scope == "series" ? text["Series_canceled"] : text["Date_canceled"]).Value,
            count => text["Arg0_dates_canceled_People_who_were_expecting_them_get_a_Discord_message", count].Value);

    private async Task<IActionResult> RunEventActionAsync<T>(ulong guildId, Guid eventId, string date,
        Func<ulong, DateOnly, Task<HappyMix.Domain.Abstractions.Result<T>>> action, Func<T, string> title, Func<T, string?> body)
    {
        if (await GetDiscordMemberAsync(guildId) is not { } member)
            return NotMember();
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return BadRequest();

        var result = await action(member.Id, day);
        TempData["EventBannerKind"] = result.IsFailure ? "error" : "success";
        TempData["EventBannerTitle"] = result.IsFailure ? text["That_didn_t_work"].Value : title(result.Value);
        TempData["EventBannerText"] = result.IsFailure ? text.Message(result.Error.Description).Value : body(result.Value);
        string back = Request.Form["returnUrl"].ToString();
        return Url.IsLocalUrl(back) && back.StartsWith('/') ? LocalRedirect(back) : Redirect($"/events/{guildId}/{eventId}?date={day:yyyy-MM-dd}");
    }

    /// <summary>Invitations waiting for an answer, dates the viewer accepted, and events the viewer organizes.</summary>
    private async Task<(IReadOnlyList<EventRow> Invitations, IReadOnlyList<EventRow> Accepted, IReadOnlyList<EventRow> Organizing)> LoadEventRowsAsync(
        ulong guildId, ulong userId)
    {
        var mine = (await sender.Send(new GetMemberEventsQuery(guildId, userId), HttpContext.RequestAborted)).Value;
        if (mine.Events.Count == 0)
            return ([], [], []);

        var now = clock.UtcNow;
        var (members, available) = await memberDirectory.GetMembersAsync(guildId);
        var names = new MemberNames(members, available);
        string Name(ulong id) => names.Name(id, key => text.Message(key));
        var groupNames = (await sender.Send(new GetEventGroupsQuery(guildId, userId), HttpContext.RequestAborted)).Value.ToDictionary(item => item.Id, item => item.Name);
        var invitations = new List<EventRow>();
        var accepted = new List<EventRow>();
        var organizing = new List<EventRow>();
        foreach (var details in mine.Events)
        {
            var zone = EventText.Zone(details.Event);
            string? group = details.Event.GroupId is { } id ? groupNames.GetValueOrDefault(id) : null;
            EventRow Row(EventOccurrence occurrence) => EventText.Row(details, occurrence, mine.ViewerId, zone, group, Name, guildId);
            var upcoming = details.Occurrences.Where(item => item.End > now).ToArray();
            if (details.Event.OrganizerId == mine.ViewerId && upcoming.FirstOrDefault(item => item.Status != OccurrenceStatus.Canceled) is { } next)
                organizing.Add(Row(next));
            if (!details.Invitees.Contains(mine.ViewerId))
                continue;
            foreach (var occurrence in upcoming)
            {
                var answer = details.Responses.FirstOrDefault(item => item.UserId == mine.ViewerId && item.Date == occurrence.Date);
                if (answer is null && occurrence.Status != OccurrenceStatus.Canceled && occurrence.Start <= now.AddDays(InvitationDays))
                    invitations.Add(Row(occurrence));
                // Canceled dates the viewer had accepted stay listed (marked canceled) until they pass.
                else if (answer?.Accepted == true)
                    accepted.Add(Row(occurrence));
            }
        }
        return (invitations.Take(12).ToArray(), accepted.Take(20).ToArray(), organizing.Take(20).ToArray());
    }

    private async Task<IActionResult> RenderEventFormAsync(ulong guildId, ulong userId, bool admin, EventForm form, string? error)
    {
        var groups = (await sender.Send(new GetEventGroupsQuery(guildId, userId), HttpContext.RequestAborted)).Value;
        if (groups.Count == 0)
            form.Audience = "people";
        var (members, available) = await memberDirectory.GetMembersAsync(guildId);
        var people = members.Values.Where(profile => profile.InServer && !profile.IsBot && profile.Id != userId)
            .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new EventPerson(profile.Id, profile.DisplayName, profile)).ToArray();
        var zone = await GetGuildZoneAsync(guildId, HttpContext.RequestAborted);
        return View("NewEvent", new EventFormPage(guildId, GuildName(guildId), admin, form, groups, people, zone.Id, error));
    }

    private async Task<string?> GroupNameAsync(ulong guildId, ulong userId, Guid? groupId)
    {
        if (groupId is not { } id)
            return null;
        var groups = (await sender.Send(new GetEventGroupsQuery(guildId, userId), HttpContext.RequestAborted)).Value;
        return groups.FirstOrDefault(item => item.Id == id)?.Name ?? text["A_group_2"];
    }

    private BannerModel? TakeEventBanner() => TempData["EventBannerTitle"] is string title
        ? new BannerModel(TempData["EventBannerKind"] as string ?? "success", title, TempData["EventBannerText"] as string)
        : null;

    private ViewResult EventNotFound(ulong guildId) => Forbidden(new ForbiddenPage(text["Event_not_available"],
        text["That_event_doesn_t_exist_or_you_aren_t_invited_to_it"], $"/events/{guildId}", text["Back_to_events"]));
}
