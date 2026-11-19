using System.Globalization;
using HappyMix.Application.Abstractions.Watch;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Group watchlists in the WatchItems table (see migration AddDiscordVotesAndWatchlist).</summary>
internal sealed class SqliteWatchStore(IDbContextFactory<HappyMixDbContext> factory) : IWatchStore
{
    public async Task<IReadOnlyList<WatchItem>> ListAsync(ulong guildId, Guid groupId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId.ToString();
        return (await db.Database.SqlQuery<Row>($"SELECT * FROM WatchItems WHERE GuildId = {guild} AND GroupId = {group}").ToListAsync())
            .Select(ToItem).ToArray();
    }

    public async Task<WatchItem?> GetAsync(ulong guildId, Guid itemId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), id = itemId.ToString();
        var row = await db.Database.SqlQuery<Row>($"SELECT * FROM WatchItems WHERE GuildId = {guild} AND Id = {id}").SingleOrDefaultAsync();
        return row is null ? null : ToItem(row);
    }

    public async Task<WatchItem> AddAsync(WatchItem item)
    {
        await using var db = await factory.CreateDbContextAsync();
        string group = item.GroupId.ToString();
        if (item.Source is { } source && item.ExternalId is { } external &&
            await db.Database.SqlQuery<Row>($"SELECT * FROM WatchItems WHERE GroupId = {group} AND Source = {source} AND ExternalId = {external}")
                .SingleOrDefaultAsync() is { } existing)
            return ToItem(existing);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WatchItems (Id, GuildId, GroupId, Kind, Title, Url, ImageUrl, Source, ExternalId, Status, Season, Episode,
                PositionSeconds, AddedBy, AddedUtc, UpdatedUtc, FinishedUtc)
            VALUES ({item.Id.ToString()}, {Id(item.GuildId)}, {group}, {item.Kind}, {item.Title}, {item.Url}, {item.ImageUrl}, {item.Source},
                {item.ExternalId}, {(int)item.Status}, {item.Season}, {item.Episode}, {item.PositionSeconds}, {Id(item.AddedBy)},
                {item.AddedAt.ToUnixTimeSeconds()}, {item.UpdatedAt.ToUnixTimeSeconds()}, {item.FinishedAt?.ToUnixTimeSeconds()})
            """);
        return item;
    }

    public async Task SaveAsync(WatchItem item)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE WatchItems SET Title = {item.Title}, Url = {item.Url}, ImageUrl = {item.ImageUrl}, Status = {(int)item.Status},
                Season = {item.Season}, Episode = {item.Episode}, PositionSeconds = {item.PositionSeconds},
                UpdatedUtc = {item.UpdatedAt.ToUnixTimeSeconds()}, FinishedUtc = {item.FinishedAt?.ToUnixTimeSeconds()}
            WHERE Id = {item.Id.ToString()} AND GuildId = {Id(item.GuildId)}
            """);
    }

    public async Task<bool> RemoveAsync(ulong guildId, Guid itemId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), id = itemId.ToString();
        return await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM WatchItems WHERE GuildId = {guild} AND Id = {id}") == 1;
    }

    private static WatchItem ToItem(Row row) => new(Guid.Parse(row.Id), Parse(row.GuildId), Guid.Parse(row.GroupId), row.Kind, row.Title,
        row.Url, row.ImageUrl, row.Source, row.ExternalId, (WatchStatus)row.Status, row.Season, row.Episode, row.PositionSeconds,
        Parse(row.AddedBy), DateTimeOffset.FromUnixTimeSeconds(row.AddedUtc), DateTimeOffset.FromUnixTimeSeconds(row.UpdatedUtc),
        row.FinishedUtc is { } finished ? DateTimeOffset.FromUnixTimeSeconds(finished) : null);

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private sealed record Row(string Id, string GuildId, string GroupId, string Kind, string Title, string? Url, string? ImageUrl,
        string? Source, string? ExternalId, int Status, int Season, int Episode, int? PositionSeconds, string AddedBy,
        long AddedUtc, long UpdatedUtc, long? FinishedUtc);
}
