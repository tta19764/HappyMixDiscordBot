using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

public sealed record NotificationRoute(string GuildId, string Scope, string TargetId, bool Enabled, string ChannelId, string Language);

public sealed class NotificationRouteStore
{
    private readonly IDbContextFactory<HappyMixDbContext> _factory;
    internal NotificationRouteStore(IDbContextFactory<HappyMixDbContext> factory) => _factory = factory;
    public async Task<NotificationRoute?> GetAsync(ulong guildId, string scope, Guid target)
    {
        await using var db = await _factory.CreateDbContextAsync();
        string guild = guildId.ToString(CultureInfo.InvariantCulture), id = target.ToString();
        return await db.Database.SqlQuery<NotificationRoute>($"SELECT * FROM NotificationRoutes WHERE GuildId={guild} AND Scope={scope} AND TargetId={id}").SingleOrDefaultAsync();
    }
    public async Task SaveAsync(NotificationRoute route)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationRoutes VALUES ({route.GuildId}, {route.Scope}, {route.TargetId}, {route.Enabled}, {route.ChannelId}, {route.Language})
            ON CONFLICT (GuildId, Scope, TargetId) DO UPDATE SET Enabled=excluded.Enabled, ChannelId=excluded.ChannelId, Language=excluded.Language
            """);
    }
    public async Task ResetAsync(ulong guildId, string scope, Guid target)
    {
        await using var db = await _factory.CreateDbContextAsync();
        string guild = guildId.ToString(CultureInfo.InvariantCulture), id = target.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM NotificationRoutes WHERE GuildId={guild} AND Scope={scope} AND TargetId={id}");
    }
    public async Task<bool> WasSentAsync(string key)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM EventChannelDelivery WHERE DeliveryKey={key}").SingleAsync() > 0;
    }
    public async Task MarkSentAsync(string key)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO EventChannelDelivery VALUES ({key})");
    }
}
