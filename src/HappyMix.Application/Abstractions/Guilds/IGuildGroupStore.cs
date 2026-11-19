namespace HappyMix.Application.Abstractions.Guilds;

public sealed record GuildGroup(
    string Name,
    ulong OrganizerId,
    ulong[] Members,
    ulong[] Invites,
    Dictionary<ulong, Guid>? InviteTokens = null,
    string? Description = null,
    ulong? RoleId = null,
    bool Open = false,
    ulong[]? JoinRequests = null,
    GameSuggestion[]? Suggestions = null)
{
    /// <summary>Durable identifier assigned by the store. Stays the same for the group's lifetime.</summary>
    public Guid Id { get; init; }

    public static string GetNameKey(string name) => name.Trim().ToUpperInvariant();
}

/// <summary>
/// A game a member suggested for the group, waiting for the organizer. <see cref="Provider"/> and <see cref="ExternalId"/>
/// identify it in a catalog when it was picked from a search (steam or igdb).
/// </summary>
public sealed record GameSuggestion(Guid Id, string Name, string? Provider, string? ExternalId, ulong SuggestedBy, DateTimeOffset At);

/// <summary>A game attached to a group. Steam-linked games carry the provider and external ID for later joins.</summary>
public sealed record GroupGame(Guid Id, Guid GroupId, string Name, string? Provider = null, string? ExternalId = null)
{
    public const string SteamProvider = "steam";
    /// <summary>Games from IGDB, which covers every platform (Riot, Epic, Battle.net, consoles, mobile).</summary>
    public const string IgdbProvider = "igdb";

    public string? SteamAppId => Provider == SteamProvider ? ExternalId : null;
}

public interface IGuildGroupStore
{
    Task<IReadOnlyList<GuildGroup>> ListAsync(ulong guildId);
    Task UpdateAsync(ulong guildId, string name, Func<GuildGroup?, GuildGroup?> update);

    /// <summary>Lists every game attached to groups in the server.</summary>
    Task<IReadOnlyList<GroupGame>> ListGamesAsync(ulong guildId);

    /// <summary>Inserts or updates a game. Returns false when the group already has that provider ID.</summary>
    Task<bool> SaveGameAsync(ulong guildId, GroupGame game);

    /// <summary>Removes a game when it belongs to the given group in the given server.</summary>
    Task<bool> RemoveGameAsync(ulong guildId, Guid groupId, Guid gameId);
}
