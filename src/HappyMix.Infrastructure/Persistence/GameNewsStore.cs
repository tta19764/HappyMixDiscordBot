using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

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

public sealed record GameNewsSubscription(string GuildId, uint AppId, string Name, string OwnerId,
    string ChannelId, string RoleId, long CreatedAt, bool Deleting = false, GameNewsSources Sources = GameNewsSources.Official);

public sealed class GameNewsStore
{
    private readonly IDbContextFactory<HappyMixDbContext> _factory;

    internal GameNewsStore(IDbContextFactory<HappyMixDbContext> factory) => _factory = factory;

    public async Task<List<GameNewsSubscription>> ListAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<GameNewsSubscription>($"SELECT * FROM GameNews").ToListAsync(ct);
    }

    public Task AddAsync(GameNewsSubscription item, CancellationToken ct) => ExecuteAsync($"""
        INSERT INTO GameNews (GuildId, AppId, Name, OwnerId, ChannelId, RoleId, CreatedAt, Sources)
        VALUES ({item.GuildId}, {item.AppId}, {item.Name}, {item.OwnerId}, {item.ChannelId}, {item.RoleId}, {item.CreatedAt}, {(int)item.Sources})
        """, ct);

    public Task SetSourcesAsync(GameNewsSubscription item, GameNewsSources sources, CancellationToken ct) =>
        ExecuteAsync($"UPDATE GameNews SET Sources = {(int)sources} WHERE GuildId = {item.GuildId} AND AppId = {item.AppId}", ct);

    public Task MarkDeletingAsync(GameNewsSubscription item, CancellationToken ct) =>
        ExecuteAsync($"UPDATE GameNews SET Deleting = 1 WHERE GuildId = {item.GuildId} AND AppId = {item.AppId}", ct);

    public async Task RemoveAsync(GameNewsSubscription item, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GameNewsDelivery WHERE GuildId = {item.GuildId} AND AppId = {item.AppId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GameNews WHERE GuildId = {item.GuildId} AND AppId = {item.AppId}", ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<HashSet<string>> DeliveredAsync(GameNewsSubscription item, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var ids = await db.Database.SqlQuery<string>($"SELECT NewsId AS Value FROM GameNewsDelivery WHERE GuildId = {item.GuildId} AND AppId = {item.AppId}").ToListAsync(ct);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    public Task RecordDeliveryAsync(GameNewsSubscription item, string id, CancellationToken ct) =>
        ExecuteAsync($"INSERT OR IGNORE INTO GameNewsDelivery VALUES ({item.GuildId}, {item.AppId}, {id})", ct);

    private async Task ExecuteAsync(FormattableString query, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(query, ct);
    }
}
