using System.Globalization;
using HappyMix.Application.Abstractions.Logs;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Stored log entries in the AppLogs table (see migration AddAppLogs).</summary>
internal sealed class SqliteLogStore(IDbContextFactory<HappyMixDbContext> factory) : ILogStore
{
    public async Task WriteAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var entry in entries)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AppLogs (AtMs, Level, Area, Category, Message, Exception, GuildId, UserId, Properties)
                VALUES ({entry.At.ToUnixTimeMilliseconds()}, {(int)entry.Level}, {entry.Area}, {entry.Category}, {entry.Message}, {entry.Exception},
                        {Id(entry.GuildId)}, {Id(entry.UserId)}, {entry.Properties})
                """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LogEntry>> ReadAsync(LogFilter filter, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        string? guild = Id(filter.GuildId);
        long since = filter.Since?.ToUnixTimeMilliseconds() ?? 0;
        long before = filter.Before ?? long.MaxValue, after = filter.After ?? 0;
        int level = (int)filter.MinimumLevel, limit = Math.Clamp(filter.Limit, 1, 500);
        string? search = string.IsNullOrWhiteSpace(filter.Search) ? null : "%" + filter.Search.Trim().Replace("%", "", StringComparison.Ordinal) + "%";
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT Id, AtMs, Level, Area, Category, Message, Exception, GuildId, UserId, Properties FROM AppLogs
            WHERE (GuildId = {guild} OR ({filter.IncludeBotWide} AND GuildId IS NULL))
              AND Level >= {level} AND AtMs >= {since} AND Id < {before} AND Id > {after}
              AND ({filter.Area} IS NULL OR Area = {filter.Area})
              AND ({search} IS NULL OR Message LIKE {search} OR Exception LIKE {search} OR Properties LIKE {search})
            ORDER BY Id DESC LIMIT {limit}
            """).ToListAsync(cancellationToken);
        return rows.Select(row => new LogEntry(row.Id, DateTimeOffset.FromUnixTimeMilliseconds(row.AtMs), (LogSeverity)row.Level, row.Area, row.Category,
            row.Message, row.Exception, Parse(row.GuildId), Parse(row.UserId), row.Properties)).ToArray();
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, int maxEntries, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        long cutoff = olderThan.ToUnixTimeMilliseconds();
        int removed = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AppLogs WHERE AtMs < {cutoff}", cancellationToken);
        removed += await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM AppLogs WHERE Id <= (SELECT Id FROM AppLogs ORDER BY Id DESC LIMIT 1 OFFSET {Math.Max(1, maxEntries)})", cancellationToken);
        return removed;
    }

    private static string? Id(ulong? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static ulong? Parse(string? value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : null;

    private sealed record Row(long Id, long AtMs, int Level, string Area, string Category, string Message, string? Exception,
        string? GuildId, string? UserId, string? Properties);
}
