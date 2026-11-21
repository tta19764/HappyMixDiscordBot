using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using HappyMix.Application.Abstractions.Profiles;
using HappyMix.Application.Profiles;
using MediatR;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace HappyMix.Bot.Web;

/// <summary>
/// The dashboard's view of who is signed in. Discord sign-in (and "Sync with Discord") saves the profile on the server;
/// the cookie carries a copy plus the time it was synced. On each request a newer saved profile, for example from a
/// sync in another browser, replaces the copy, so nobody has to sign in again to see their Discord changes.
/// </summary>
public static class DiscordProfileClaims
{
    public const string DisplayNameClaim = "hm:display-name";
    public const string AvatarClaim = "hm:avatar";
    public const string SyncedClaim = "hm:synced";

    private static readonly string[] Owned =
    [
        ClaimTypes.Name, DisplayNameClaim, AvatarClaim, SyncedClaim, DiscordConnections.CheckedClaim, DiscordConnections.SteamIdClaim,
        DiscordConnections.SteamNameClaim, DiscordConnections.SteamVerifiedClaim
    ];

    /// <summary>Reads the Discord user and connections, saves them and puts them in the new sign-in.</summary>
    public static async Task OnCreatingTicketAsync(OAuthCreatingTicketContext context)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();
        using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
        context.RunClaimActions(user.RootElement);
        if (context.Identity is not { } identity || UserId(identity) is not { } userId)
            return;

        var (connectionsChecked, steam) = await DiscordConnections.ReadSteamAsync(context);
        var sender = context.HttpContext.RequestServices.GetRequiredService<ISender>();
        var saved = await sender.Send(new SaveDiscordProfileCommand(userId, Text(user.RootElement, "username") ?? userId.ToString(CultureInfo.InvariantCulture),
            Text(user.RootElement, "global_name"), Text(user.RootElement, "avatar"), steam, connectionsChecked), context.HttpContext.RequestAborted);
        if (saved.IsSuccess)
            Apply(identity, saved.Value);
    }

    /// <summary>Swaps in the saved profile when it is newer than the copy in the cookie.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || UserId(identity) is not { } userId)
            return;
        var sender = context.HttpContext.RequestServices.GetRequiredService<ISender>();
        if ((await sender.Send(new GetUserProfileQuery(userId), context.HttpContext.RequestAborted)).Value is not { } profile
            || SyncedAt(context.Principal) is { } synced && profile.SyncedAt.ToUnixTimeMilliseconds() <= synced.ToUnixTimeMilliseconds())
            return;
        var fresh = new ClaimsIdentity(identity.Claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
        Apply(fresh, profile);
        context.ReplacePrincipal(new ClaimsPrincipal(fresh));
        context.ShouldRenew = true;
    }

    public static void Apply(ClaimsIdentity identity, UserProfile profile)
    {
        foreach (var claim in identity.Claims.Where(claim => Owned.Contains(claim.Type)).ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(ClaimTypes.Name, profile.UserName));
        if (profile.DisplayName is { } displayName)
            identity.AddClaim(new Claim(DisplayNameClaim, displayName));
        if (profile.Avatar is { } avatar)
            identity.AddClaim(new Claim(AvatarClaim, avatar));
        identity.AddClaim(new Claim(SyncedClaim, profile.SyncedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        if (profile.ConnectionsChecked)
            identity.AddClaim(new Claim(DiscordConnections.CheckedClaim, "1"));
        if (profile.Steam is { } steam)
        {
            identity.AddClaim(new Claim(DiscordConnections.SteamIdClaim, steam.SteamId));
            identity.AddClaim(new Claim(DiscordConnections.SteamNameClaim, steam.Name));
            identity.AddClaim(new Claim(DiscordConnections.SteamVerifiedClaim, steam.Verified ? "1" : "0"));
        }
    }

    public static DateTimeOffset? SyncedAt(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(SyncedClaim), NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    /// <summary>Discord's display name, falling back to the user name.</summary>
    public static string? Name(ClaimsPrincipal user) => user.FindFirstValue(DisplayNameClaim) ?? user.FindFirstValue(ClaimTypes.Name);

    /// <summary>The avatar picture on Discord's image server, or null for people without one.</summary>
    public static string? AvatarUrl(ClaimsPrincipal user, int size = 64) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) is { } id && user.FindFirstValue(AvatarClaim) is { Length: > 0 } hash
            && hash.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? string.Create(CultureInfo.InvariantCulture,
                $"https://cdn.discordapp.com/avatars/{id}/{hash}.{(hash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png")}?size={size}")
            : null;

    private static ulong? UserId(ClaimsIdentity identity) =>
        ulong.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
