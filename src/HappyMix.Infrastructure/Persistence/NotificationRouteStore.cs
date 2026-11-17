using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>
/// Channel notifications for a group or event. <see cref="ReminderMinutes"/> null uses the deployment's lead time;
/// <see cref="Updates"/> also posts when events are scheduled, confirmed or canceled.
/// </summary>
public sealed record NotificationRoute(string GuildId, string Scope, string TargetId, bool Enabled, string ChannelId, string Language,
    int? ReminderMinutes = null, bool Updates = false)
{
    public ulong Channel() => ulong.TryParse(ChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0;
}

public sealed class NotificationRouteStore
{
    private readonly IDbContextFactory<HappyMixDbContext> _factory;
    internal NotificationRouteStore(IDbContextFactory<HappyMixDbContext> factory) => _factory = factory;
    public async Task<NotificationRoute?> GetAsync(ulong guildId, string scope, Guid target)
    {
        await using var db = await _factory.CreateDbContextAsync();
        string guild = guildId.ToString(CultureInfo.InvariantCulture), id = target.ToString();
        return await db.Database.SqlQuery<NotificationRoute>(
            $"SELECT GuildId, Scope, TargetId, Enabled, ChannelId, Language, ReminderMinutes, Updates FROM NotificationRoutes WHERE GuildId={guild} AND Scope={scope} AND TargetId={id}")
            .SingleOrDefaultAsync();
    }
    public async Task SaveAsync(NotificationRoute route)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationRoutes (GuildId, Scope, TargetId, Enabled, ChannelId, Language, ReminderMinutes, Updates)
            VALUES ({route.GuildId}, {route.Scope}, {route.TargetId}, {route.Enabled}, {route.ChannelId}, {route.Language}, {route.ReminderMinutes}, {route.Updates})
            ON CONFLICT (GuildId, Scope, TargetId) DO UPDATE SET Enabled=excluded.Enabled, ChannelId=excluded.ChannelId, Language=excluded.Language,
                ReminderMinutes=excluded.ReminderMinutes, Updates=excluded.Updates
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
