namespace HappyMix.Application.Abstractions.Profiles;

/// <summary>The Steam account connected to someone's Discord profile, as Discord reported it.</summary>
public sealed record DiscordSteamConnection(string SteamId, string Name, bool Verified);

/// <summary>
/// What the dashboard knows about a person from Discord, saved on the server at every Discord sign-in or "Sync with
/// Discord", so signed-in browsers pick up changes without signing in again.
/// </summary>
/// <param name="DisplayName">Discord's display name (global name), when the person set one.</param>
/// <param name="Avatar">Discord's avatar hash, when the person has an avatar.</param>
/// <param name="ConnectionsChecked">Whether Discord answered the connections request, so "no Steam" is known rather than unknown.</param>
public sealed record UserProfile(ulong UserId, string UserName, string? DisplayName, string? Avatar, DiscordSteamConnection? Steam,
    bool ConnectionsChecked, DateTimeOffset SyncedAt)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;
}

public interface IUserProfileStore
{
    Task<UserProfile?> GetAsync(ulong userId);
    Task SaveAsync(UserProfile profile);
}
