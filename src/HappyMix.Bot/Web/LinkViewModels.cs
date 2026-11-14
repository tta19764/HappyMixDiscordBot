using System.Globalization;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Guilds;
using HappyMix.Bot.Services;

namespace HappyMix.Bot.Web;

/// <summary>A member's primary avatar with smaller overlapping avatars for linked accounts.</summary>
public sealed record MemberStackModel(MemberProfile Primary, string Name, IReadOnlyList<(MemberProfile Profile, string Name)> Linked,
    bool Small = false)
{
    public const int Visible = 2;
}

public sealed record LinksPage(ulong GuildId, string GuildName, GroupViewer Viewer, LinkedAccountsView View, MemberNames Names,
    string? NewLinkUrl, DateTimeOffset? NewLinkExpires, BannerModel? Banner, string TimeZone);

public sealed record ApproveLinkPage(ulong GuildId, string GuildName, GroupViewer Viewer, Guid RequestId, string Token,
    LinkPreview? Preview, string? Error, bool SameAccount, MemberNames Names, string TimeZone);

public sealed record UnlinkPage(ulong GuildId, string GuildName, GroupViewer Viewer, UnlinkPreview Preview, MemberNames Names);

public static class MemberStacks
{
    /// <summary>Builds the avatar stack for a member and its linked accounts, with safe fallback names.</summary>
    public static MemberStackModel For(MemberNames names, ulong member, IEnumerable<ulong> alternates, Func<string, string> localize,
        bool small = false) =>
        new(names.Get(member), names.Name(member, localize),
            alternates.Select(id => (names.Get(id), names.Name(id, localize))).ToArray(), small);

    public static MemberStackModel For(GroupPage page, ulong member, Func<string, string> localize, bool small = false) =>
        For(page.Names, member, page.Detail.LinkedAccounts.TryGetValue(member, out var alternates) ? alternates : [], localize, small);

    /// <summary>A window shown in the server's time zone, for example "Mon 26 Oct 19:00–21:00".</summary>
    public static string Window(AvailabilityWindow window, string zoneId, Func<string, string> localize)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        var start = TimeZoneInfo.ConvertTime(window.Start, zone);
        var end = TimeZoneInfo.ConvertTime(window.End, zone);
        var culture = CultureInfo.CurrentCulture;
        return start.ToString("ddd d MMM HH:mm", culture) + "–" +
            end.ToString(end.Date == start.Date ? "HH:mm" : "ddd HH:mm", culture) + " · " + localize(window.Tentative ? "Tentative" : "Definite");
    }

    public static string Weekly(WeeklyAvailability day, Func<string, string> localize) => (day.IsFullDay
        ? "00:00–24:00"
        : $"{AvailabilityFormat.Time(day.Start)}–{AvailabilityFormat.Time(day.End)}" + (day.TimeZone is { Length: > 0 } ? $" · {day.TimeZone}" : "")) + " · " + localize(day.Tentative ? "Tentative" : "Definite");

}
