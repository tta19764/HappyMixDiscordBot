namespace HappyMix.Application.Abstractions.Players;

/// <summary>How a player feels about a game, used for group game decisions.</summary>
public enum GamePreference { Want = 1, Can = 2, Wont = 3 }

/// <summary>
/// One of a Discord account's Steam accounts. <see cref="Verified"/> means the person signed in through Steam, which proves
/// the account is theirs; a pasted SteamID is unverified and is enough for importing public game lists only. A person
/// can link twink accounts next to their <see cref="IsMain"/> account; libraries of all of them are imported together.
/// </summary>
public sealed record SteamAccount(ulong UserId, string SteamId, bool Verified, DateTimeOffset LinkedAt, DateTimeOffset? ImportedAt)
{
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId}";
    /// <summary>The main account (the first one linked, unless the person picked another); the others are twinks.</summary>
    public bool IsMain { get; init; } = true;
    /// <summary>Whether this account's Dota 2 and CS2 stats are shown to the person's groups while they share stats.</summary>
    public bool ShowStats { get; init; } = true;
    /// <summary>The person's own name for the account, such as "Smurf" or "Party account".</summary>
    public string? Label { get; init; }
}

/// <summary>
/// A game on a player's list. <see cref="Key"/> identifies it across sources: <c>steam:730</c>, <c>igdb:115</c>, or
/// <c>name:counter-strike 2</c> for a name typed by hand. <see cref="FromSteam"/> marks rows imported from the Steam library,
/// which unlinking Steam removes; playtimes are in minutes.
/// </summary>
public sealed record PlayerGame(ulong UserId, string Key, string Name, string? Provider, string? ExternalId, GamePreference Preference,
    bool FromSteam, int? PlaytimeMinutes, int? RecentMinutes, DateTimeOffset UpdatedAt);

/// <summary>Players' game lists and Steam links. Data belongs to the Discord account, not to a server.</summary>
public interface IPlayerGameStore
{
    /// <summary>The person's main Steam account.</summary>
    Task<SteamAccount?> GetSteamAsync(ulong userId);
    /// <summary>All the person's Steam accounts, main first, then in the order they were linked.</summary>
    Task<IReadOnlyList<SteamAccount>> ListSteamAsync(ulong userId);
    Task<SteamAccount?> FindSteamOwnerAsync(string steamId);
    Task SaveSteamAsync(SteamAccount account);

    /// <summary>Removes every Steam link of the person and every game imported from them.</summary>
    Task RemoveSteamAsync(ulong userId);

    /// <summary>Removes one Steam account; when it was the main one, the oldest remaining account becomes main.</summary>
    Task RemoveSteamAccountAsync(ulong userId, string steamId);

    /// <summary>Makes one of the person's accounts the main one.</summary>
    Task SetMainSteamAsync(ulong userId, string steamId);

    Task<IReadOnlyList<PlayerGame>> ListAsync(ulong userId);
    Task<IReadOnlyList<PlayerGame>> ListForAsync(IReadOnlyCollection<ulong> userIds);
    /// <summary>Everyone's entries of one game imported from Steam (by key, or by name), for "who already has it".</summary>
    Task<IReadOnlyList<PlayerGame>> ListOwnersAsync(string gameKey, string gameName);
    Task SaveAsync(PlayerGame game);
    Task SaveManyAsync(IReadOnlyCollection<PlayerGame> games);
    Task<bool> RemoveAsync(ulong userId, string key);
}

/// <summary>An owned game from a Steam library.</summary>
public sealed record SteamOwnedGame(string AppId, string Name, int PlaytimeMinutes, int RecentMinutes);

public enum SteamLibraryStatus { Ok, Private, NotConfigured, Unavailable }

public sealed record SteamLibrary(SteamLibraryStatus Status, IReadOnlyList<SteamOwnedGame> Games);

/// <summary>Steam lookups: turning what people paste into a SteamID64, and reading public libraries.</summary>
public interface ISteamDirectory
{
    /// <summary>Whether a Steam Web API key is configured, which library imports need.</summary>
    bool CanImport { get; }

    /// <summary>A SteamID64 from a SteamID64, a profile link (/profiles/… or /id/…) or a custom URL name; null when it can't be found.</summary>
    Task<string?> ResolveAsync(string input, CancellationToken cancellationToken);

    Task<SteamLibrary> GetOwnedGamesAsync(string steamId, CancellationToken cancellationToken);

    /// <summary>
    /// Checks a Steam sign-in response (the <c>openid.*</c> values Steam sent back) with Steam itself and returns the proven
    /// SteamID64, or null when Steam doesn't confirm it or it was meant for another address than <paramref name="returnTo"/>.
    /// </summary>
    Task<string?> VerifySignInAsync(IReadOnlyDictionary<string, string> response, string returnTo, CancellationToken cancellationToken);

    /// <summary>
    /// What these accounts are playing right now, by SteamID64 (absent when not in a game). Needs the API key and public
    /// profiles; an empty result when Steam can't be asked.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetPlayingAsync(IReadOnlyCollection<string> steamIds, CancellationToken cancellationToken);
}
