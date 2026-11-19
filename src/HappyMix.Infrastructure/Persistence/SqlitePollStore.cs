using System.Globalization;
using HappyMix.Application.Abstractions.Polls;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>Group tags and polls in plain SQLite tables (see migration AddGroupTagsAndPolls).</summary>
internal sealed class SqlitePollStore(IDbContextFactory<HappyMixDbContext> factory) : IPollStore
{
    public async Task<IReadOnlyList<string>> GetTagsAsync(ulong guildId, Guid groupId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId.ToString();
        return await db.Database.SqlQuery<string>($"SELECT Tag AS Value FROM GroupTags WHERE GuildId = {guild} AND GroupId = {group} ORDER BY Position")
            .ToListAsync();
    }

    public async Task SetTagsAsync(ulong guildId, Guid groupId, IReadOnlyCollection<string> tags)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        string guild = Id(guildId), group = groupId.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM GroupTags WHERE GuildId = {guild} AND GroupId = {group}");
        int position = 0;
        foreach (string tag in tags)
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO GroupTags (GuildId, GroupId, Tag, Position) VALUES ({guild}, {group}, {tag}, {position++})");
        await transaction.CommitAsync();
    }

    public async Task CreateAsync(Poll poll)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        string id = poll.Id.ToString(), group = poll.GroupId?.ToString() ?? "";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Polls (Id, GuildId, GroupId, CreatorId, Question, Multiple, Everyone, CreatedUtc, ClosesUtc, ClosedUtc, ChannelId)
            VALUES ({id}, {Id(poll.GuildId)}, {group}, {Id(poll.CreatorId)}, {poll.Question}, {poll.Multiple}, {poll.Everyone},
                    {poll.CreatedAt.ToUnixTimeSeconds()}, {poll.ClosesAt.ToUnixTimeSeconds()}, NULL, {(poll.ChannelId is { } channel ? Id(channel) : null)})
            """);
        foreach (var option in poll.Options)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO PollOptions (PollId, Position, Label, Kind, Url, Source, ExternalId)
                VALUES ({id}, {option.Position}, {option.Label}, {option.Kind}, {option.Url}, {option.Media?.Source}, {option.Media?.ExternalId})
                """);
        foreach (ulong member in poll.Audience.Distinct())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PollAudience (PollId, UserId) VALUES ({id}, {Id(member)})");
        await transaction.CommitAsync();
    }

    public async Task<Poll?> GetAsync(ulong guildId, Guid pollId)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), id = pollId.ToString();
        var rows = await db.Database.SqlQuery<PollRow>($"SELECT * FROM Polls WHERE GuildId = {guild} AND Id = {id}").ToListAsync();
        return (await LoadAsync(db, rows)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<Poll>> ListAsync(ulong guildId, Guid? groupId, int closedLimit, DateTimeOffset now)
    {
        await using var db = await factory.CreateDbContextAsync();
        string guild = Id(guildId), group = groupId?.ToString() ?? "";
        long stamp = now.ToUnixTimeSeconds();
        var open = await db.Database.SqlQuery<PollRow>(
            $"SELECT * FROM Polls WHERE GuildId = {guild} AND GroupId = {group} AND ClosedUtc IS NULL AND ClosesUtc > {stamp}").ToListAsync();
        var ended = await db.Database.SqlQuery<PollRow>($"""
            SELECT * FROM Polls WHERE GuildId = {guild} AND GroupId = {group} AND (ClosedUtc IS NOT NULL OR ClosesUtc <= {stamp})
            ORDER BY MIN(COALESCE(ClosedUtc, ClosesUtc), ClosesUtc) DESC LIMIT {closedLimit}
            """).ToListAsync();
        return await LoadAsync(db, [.. open.OrderBy(row => row.ClosesUtc), .. ended]);
    }

    public async Task VoteAsync(Guid pollId, ulong userId, IReadOnlyCollection<int> positions)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        string id = pollId.ToString(), user = Id(userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM PollVotes WHERE PollId = {id} AND UserId = {user}");
        foreach (int position in positions.Distinct())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PollVotes (PollId, UserId, Position) VALUES ({id}, {user}, {position})");
        await transaction.CommitAsync();
    }

    public async Task<bool> CloseAsync(Guid pollId, DateTimeOffset now)
    {
        await using var db = await factory.CreateDbContextAsync();
        long stamp = now.ToUnixTimeSeconds();
        string id = pollId.ToString();
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Polls SET ClosedUtc = {stamp} WHERE Id = {id} AND ClosedUtc IS NULL AND ClosesUtc > {stamp}") == 1;
    }

    public async Task<IReadOnlyList<Poll>> ListDiscordWorkAsync(DateTimeOffset now)
    {
        await using var db = await factory.CreateDbContextAsync();
        long stamp = now.ToUnixTimeSeconds();
        var rows = await db.Database.SqlQuery<PollRow>($"""
            SELECT * FROM Polls WHERE ChannelId IS NOT NULL AND (
                (MessageId IS NULL AND ClosedUtc IS NULL AND ClosesUtc > {stamp}) OR
                (MessageId IS NOT NULL AND Finalized = 0 AND (ClosedUtc IS NOT NULL OR ClosesUtc <= {stamp})))
            LIMIT 50
            """).ToListAsync();
        return await LoadAsync(db, rows);
    }

    public async Task SetMessageAsync(Guid pollId, ulong channelId, ulong? messageId, bool finalized)
    {
        await using var db = await factory.CreateDbContextAsync();
        string id = pollId.ToString(), channel = Id(channelId);
        string? message = messageId is { } value ? Id(value) : null;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Polls SET ChannelId = {channel}, MessageId = {message}, Finalized = {finalized} WHERE Id = {id}");
    }

    private static async Task<IReadOnlyList<Poll>> LoadAsync(HappyMixDbContext db, List<PollRow> rows)
    {
        var result = new List<Poll>(rows.Count);
        foreach (var row in rows)
        {
            var options = await db.Database.SqlQuery<OptionRow>(
                $"SELECT Position, Label, Kind, Url, Source, ExternalId FROM PollOptions WHERE PollId = {row.Id} ORDER BY Position").ToListAsync();
            var audience = await db.Database.SqlQuery<string>($"SELECT UserId AS Value FROM PollAudience WHERE PollId = {row.Id}").ToListAsync();
            var votes = await db.Database.SqlQuery<VoteRow>($"SELECT UserId, Position FROM PollVotes WHERE PollId = {row.Id}").ToListAsync();
            result.Add(new Poll(Guid.Parse(row.Id), Parse(row.GuildId), row.GroupId.Length == 0 ? null : Guid.Parse(row.GroupId), Parse(row.CreatorId),
                row.Question, row.Multiple, row.Everyone, DateTimeOffset.FromUnixTimeSeconds(row.CreatedUtc), DateTimeOffset.FromUnixTimeSeconds(row.ClosesUtc),
                row.ClosedUtc is { } closed ? DateTimeOffset.FromUnixTimeSeconds(closed) : null,
                options.Select(item => new PollOption(item.Position, item.Label, item.Kind, item.Url,
                    item.Source is { } source && item.ExternalId is { } external ? new(source, external) : null)).ToArray(),
                audience.Select(Parse).ToArray(),
                votes.Select(item => new PollVote(Parse(item.UserId), item.Position)).ToArray())
            {
                ChannelId = row.ChannelId is { } channel ? Parse(channel) : null,
                MessageId = row.MessageId is { } message ? Parse(message) : null,
                Finalized = row.Finalized
            });
        }
        return result;
    }

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);

    private sealed record PollRow(string Id, string GuildId, string GroupId, string CreatorId, string Question, bool Multiple, bool Everyone,
        long CreatedUtc, long ClosesUtc, long? ClosedUtc, string? ChannelId, string? MessageId, bool Finalized);
    private sealed record OptionRow(int Position, string Label, string Kind, string? Url, string? Source, string? ExternalId);
    private sealed record VoteRow(string UserId, int Position);
}
