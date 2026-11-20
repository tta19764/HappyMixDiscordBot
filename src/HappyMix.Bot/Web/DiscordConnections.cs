using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using HappyMix.Application.Players;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace HappyMix.Bot.Web;

/// <summary>
/// The Steam account connected to a person's Discord profile, read once at dashboard sign-in through the
/// <c>connections</c> scope and kept in the sign-in cookie. Reading it is best effort: sign-in never fails because of it.
/// </summary>
public static class DiscordConnections
{
    public const string SteamIdClaim = "hm:steam-id";
    public const string SteamNameClaim = "hm:steam-name";
    public const string SteamVerifiedClaim = "hm:steam-verified";
    /// <summary>Set when the sign-in included the connections scope, so the page can tell "none connected" from "not asked yet".</summary>
    public const string CheckedClaim = "hm:connections";

    public static async Task AddSteamClaimsAsync(OAuthCreatingTicketContext context)
    {
        if (context.Identity is not { } identity)
            return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me/connections");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
                return;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
            identity.AddClaim(new Claim(CheckedClaim, "1"));
            if (Steam(document.RootElement) is { } steam)
            {
                identity.AddClaim(new Claim(SteamIdClaim, steam.Id));
                identity.AddClaim(new Claim(SteamNameClaim, steam.Name));
                identity.AddClaim(new Claim(SteamVerifiedClaim, steam.Verified ? "1" : "0"));
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Without connections, My games still offers pasting a SteamID and Steam sign-in.
        }
    }

    /// <summary>The first Steam connection with a valid SteamID64, from Discord's connections list.</summary>
    public static (string Id, string Name, bool Verified)? Steam(JsonElement connections)
    {
        if (connections.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in connections.EnumerateArray())
            if (item.TryGetProperty("type", out var type) && type.GetString() == "steam"
                && item.TryGetProperty("id", out var id) && SteamIds.IsSteamId64(id.GetString()))
                return (id.GetString()!, item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    item.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True);
        return null;
    }

    public static (string Id, string Name, bool Verified)? FromUser(ClaimsPrincipal user) =>
        user.FindFirstValue(SteamIdClaim) is { } id && SteamIds.IsSteamId64(id)
            ? (id, user.FindFirstValue(SteamNameClaim) ?? "", user.FindFirstValue(SteamVerifiedClaim) == "1")
            : null;

    public static bool Checked(ClaimsPrincipal user) => user.HasClaim(claim => claim.Type == CheckedClaim);
}
