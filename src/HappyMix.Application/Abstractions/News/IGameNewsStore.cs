namespace HappyMix.Application.Abstractions.News;

/// <summary>Which kinds of Steam news a subscription posts.</summary>
[Flags]
public enum GameNewsSources
{
    None = 0,
    /// <summary>The developer's posts tagged as patch notes on the game's Steam page.</summary>
    PatchNotes = 1,
    /// <summary>The developer's other posts on the game's Steam page: events, announcements, roadmaps.</summary>
    Announcements = 2,
    /// <summary>Articles from outside sites Steam lists for the game (PC Gamer, PCGamesN, Rock Paper Shotgun…).</summary>
    Press = 4,
    Official = PatchNotes | Announcements,
    All = PatchNotes | Announcements | Press
}

/// <summary>A game news subscription: a Steam game, its news channel and the role people take to be pinged.</summary>
public sealed record GameNewsSubscription(string GuildId, uint AppId, string Name, string OwnerId,
    string ChannelId, string RoleId, long CreatedAt, bool Deleting = false, GameNewsSources Sources = GameNewsSources.Official);

public interface IGameNewsStore
{
    Task<List<GameNewsSubscription>> ListAsync(CancellationToken ct);
    Task AddAsync(GameNewsSubscription item, CancellationToken ct);
    Task MarkDeletingAsync(GameNewsSubscription item, CancellationToken ct);
    Task RemoveAsync(GameNewsSubscription item, CancellationToken ct);
    Task<HashSet<string>> DeliveredAsync(GameNewsSubscription item, CancellationToken ct);
    Task RecordDeliveryAsync(GameNewsSubscription item, string id, CancellationToken ct);
    Task SetSourcesAsync(GameNewsSubscription item, GameNewsSources sources, CancellationToken ct);
}

/// <summary>A Steam news post's id and kind, used to skip posts that were out before a kind was turned on.</summary>
public sealed record GameNewsPost(string Id, GameNewsSources Kind);

/// <summary>A refusal from Discord or Steam with a message meant for the person (permissions, Steam not answering).</summary>
public sealed class GameNewsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Discord and Steam side effects of game news: the channel and role, people's roles, and reading posts.</summary>
public interface IGameNewsGateway
{
    /// <summary>Creates the role and the news channel, checks the bot can post there and gives the creator the role.</summary>
    Task<(ulong ChannelId, ulong RoleId)> CreateAsync(ulong guildId, ulong ownerId, uint appId, string name, CancellationToken ct);

    /// <summary>Deletes the channel and the role; ones already gone are fine.</summary>
    Task DeleteAsync(ulong guildId, ulong channelId, ulong roleId, CancellationToken ct);

    Task SetRoleAsync(ulong guildId, ulong userId, ulong roleId, bool add, CancellationToken ct);

    /// <summary>Posts Steam lists for the game since <paramref name="since"/> (Unix seconds).</summary>
    Task<IReadOnlyList<GameNewsPost>> ReadPostsAsync(uint appId, long since, CancellationToken ct);
}
