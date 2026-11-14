using System.Globalization;
using System.Text.Json;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

internal sealed class SqliteAccountLinkStore(IDbContextFactory<HappyMixDbContext> factory) : IAccountLinkStore
{
    public async Task<IReadOnlyList<AccountLink>> ListAsync(ulong guildId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = Id(guildId);
        var rows = await context.Set<AccountLinkRecord>().Where(row => row.GuildId == guild).ToListAsync();
        return rows.Select(row => new AccountLink(Parse(row.PrimaryId), Parse(row.AlternateId), Instant(row.LinkedUtc)))
            .OrderBy(link => link.LinkedAt).ToArray();
    }

    public async Task<AccountLinkRequest> CreateRequestAsync(ulong guildId, ulong primaryId, byte[] tokenHash,
        DateTimeOffset now, DateTimeOffset expiresAt)
    {
        await using var context = await factory.CreateDbContextAsync();
        var row = new AccountLinkRequestRecord
        {
            Id = Guid.NewGuid(),
            GuildId = Id(guildId),
            PrimaryId = Id(primaryId),
            TokenHash = tokenHash,
            CreatedUtc = now.UtcTicks,
            ExpiresUtc = expiresAt.UtcTicks
        };
        context.Add(row);
        await context.SaveChangesAsync();
        return ToRequest(row);
    }

    public async Task<AccountLinkRequest?> GetRequestAsync(ulong guildId, Guid requestId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = Id(guildId);
        var row = await context.Set<AccountLinkRequestRecord>().SingleOrDefaultAsync(item => item.Id == requestId && item.GuildId == guild);
        return row is null ? null : ToRequest(row);
    }

    public async Task<IReadOnlyList<AccountLinkRequest>> ListRequestsAsync(ulong guildId, ulong primaryId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = Id(guildId);
        string primary = Id(primaryId);
        var rows = await context.Set<AccountLinkRequestRecord>()
            .Where(row => row.GuildId == guild && row.PrimaryId == primary).OrderByDescending(row => row.CreatedUtc).ToListAsync();
        return rows.Select(ToRequest).ToArray();
    }

    public async Task<bool> RevokeRequestAsync(ulong guildId, ulong primaryId, Guid requestId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = Id(guildId);
        string primary = Id(primaryId);
        return await context.Set<AccountLinkRequestRecord>()
            .Where(row => row.Id == requestId && row.GuildId == guild && row.PrimaryId == primary && row.ConsumedUtc == null && !row.Revoked)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Revoked, true)) > 0;
    }

    public async Task<LinkOutcome> LinkAsync(ulong guildId, LinkPlan plan)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        string guild = Id(guildId);
        string primary = Id(plan.PrimaryId);
        string alternate = Id(plan.AlternateId);
        long now = plan.Now.UtcTicks;
        var links = context.Set<AccountLinkRecord>();
        var requests = context.Set<AccountLinkRequestRecord>();

        var request = await requests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == plan.RequestId && row.GuildId == guild);
        if (request is null || request.PrimaryId != primary)
            return LinkOutcome.RequestUnavailable;
        if (request.ConsumedUtc is not null)
            return request.ConsumedBy == alternate && await links.AnyAsync(row => row.GuildId == guild &&
                row.AlternateId == alternate && row.PrimaryId == primary && row.RequestId == plan.RequestId)
                ? LinkOutcome.AlreadyLinked : LinkOutcome.RequestUnavailable;
        if (request.Revoked || request.ExpiresUtc <= now)
            return LinkOutcome.RequestUnavailable;

        // Re-check the family shape inside the transaction: no chains, no second primary, no duplicates.
        if (await links.AnyAsync(row => row.GuildId == guild && (row.AlternateId == alternate || row.AlternateId == primary ||
                row.PrimaryId == alternate)))
            return LinkOutcome.AccountUnavailable;

        // Single use: only one approval can flip ConsumedUtc from null.
        int consumed = await requests
            .Where(row => row.Id == plan.RequestId && row.ConsumedUtc == null && !row.Revoked && row.ExpiresUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ConsumedUtc, now).SetProperty(row => row.ConsumedBy, alternate));
        if (consumed != 1)
            return LinkOutcome.RequestUnavailable;

        links.Add(new AccountLinkRecord
        {
            GuildId = guild,
            AlternateId = alternate,
            PrimaryId = primary,
            RequestId = plan.RequestId,
            LinkedUtc = now,
            Snapshot = JsonSerializer.Serialize(plan.AlternateSnapshot)
        });

        var windows = context.Set<AvailabilityRecord>();
        var moved = plan.AlternateWindowsToMove.ToArray();
        var removed = plan.PrimaryWindowsToRemove.ToArray();
        await windows.Where(row => row.GuildId == guild && row.UserId == primary && removed.Contains(row.Id)).ExecuteDeleteAsync();
        await windows.Where(row => row.GuildId == guild && row.UserId == alternate && moved.Contains(row.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.UserId, primary));
        // Whatever the alternate kept apart from the moved windows lives on in the snapshot.
        await windows.Where(row => row.GuildId == guild && row.UserId == alternate).ExecuteDeleteAsync();

        var weekly = context.Set<WeeklyAvailabilityRecord>();
        foreach (var day in plan.PrimaryWeeklyToSet)
        {
            int dayNumber = (int)day.Day;
            await weekly.Where(row => row.GuildId == guild && row.UserId == primary && row.Day == dayNumber).ExecuteDeleteAsync();
            weekly.Add(new WeeklyAvailabilityRecord
            {
                GuildId = guild,
                UserId = primary,
                Day = dayNumber,
                StartTicks = day.Start.Ticks,
                EndTicks = day.End.Ticks,
                TimeZone = day.TimeZone
            });
        }
        await weekly.Where(row => row.GuildId == guild && row.UserId == alternate).ExecuteDeleteAsync();

        foreach (var row in await context.Set<GuildGroupRecord>().Where(row => row.GuildId == guild).ToListAsync())
        {
            var group = JsonSerializer.Deserialize<GuildGroup>(row.Data)!;
            var updated = plan.UpdateGroup(group);
            if (!ReferenceEquals(updated, group))
                row.Data = JsonSerializer.Serialize(updated with { Id = Guid.Empty });
        }

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return LinkOutcome.Linked;
    }

    public async Task<AvailabilitySnapshot?> GetSnapshotAsync(ulong guildId, ulong alternateId)
    {
        await using var context = await factory.CreateDbContextAsync();
        string guild = Id(guildId);
        string alternate = Id(alternateId);
        string? snapshot = await context.Set<AccountLinkRecord>().Where(row => row.GuildId == guild && row.AlternateId == alternate)
            .Select(row => row.Snapshot).SingleOrDefaultAsync();
        return snapshot is null ? null : JsonSerializer.Deserialize<AvailabilitySnapshot>(snapshot) ?? AvailabilitySnapshot.Empty;
    }

    public async Task<bool> UnlinkAsync(ulong guildId, ulong primaryId, ulong alternateId, AvailabilitySnapshot restore)
    {
        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        string guild = Id(guildId);
        string primary = Id(primaryId);
        string alternate = Id(alternateId);
        if (await context.Set<AccountLinkRecord>()
                .Where(row => row.GuildId == guild && row.AlternateId == alternate && row.PrimaryId == primary).ExecuteDeleteAsync() == 0)
            return false;

        var weekly = context.Set<WeeklyAvailabilityRecord>();
        foreach (var day in restore.Weekly)
        {
            int dayNumber = (int)day.Day;
            await weekly.Where(row => row.GuildId == guild && row.UserId == alternate && row.Day == dayNumber).ExecuteDeleteAsync();
            weekly.Add(new WeeklyAvailabilityRecord
            {
                GuildId = guild, UserId = alternate, Day = dayNumber,
                StartTicks = day.Start.Ticks, EndTicks = day.End.Ticks, TimeZone = day.TimeZone
            });
        }

        foreach (var window in restore.Windows)
            context.Add(new AvailabilityRecord
            {
                Id = Guid.NewGuid(), GuildId = guild, UserId = alternate,
                StartUtc = window.Start.UtcTicks, EndUtc = window.End.UtcTicks
            });

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);
    private static DateTimeOffset Instant(long ticks) => new(ticks, TimeSpan.Zero);

    private static AccountLinkRequest ToRequest(AccountLinkRequestRecord row) => new(row.Id, Parse(row.PrimaryId), row.TokenHash,
        Instant(row.CreatedUtc), Instant(row.ExpiresUtc), row.ConsumedUtc is { } consumed ? Instant(consumed) : null,
        row.ConsumedBy is { } by ? Parse(by) : null, row.Revoked);
}

internal sealed class AccountLinkRecord
{
    public required string GuildId { get; init; }
    public required string AlternateId { get; init; }
    public required string PrimaryId { get; init; }
    public Guid RequestId { get; init; }
    public long LinkedUtc { get; init; }
    public required string Snapshot { get; init; }
}

internal sealed class AccountLinkRequestRecord
{
    public Guid Id { get; init; }
    public required string GuildId { get; init; }
    public required string PrimaryId { get; init; }
    public required byte[] TokenHash { get; init; }
    public long CreatedUtc { get; init; }
    public long ExpiresUtc { get; init; }
    public long? ConsumedUtc { get; set; }
    public string? ConsumedBy { get; set; }
    public bool Revoked { get; set; }
}
