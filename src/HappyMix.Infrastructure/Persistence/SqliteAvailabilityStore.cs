using System.Globalization;
using HappyMix.Application.Abstractions.Guilds;
using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

internal sealed class SqliteAvailabilityStore(IDbContextFactory<HappyMixDbContext> factory) : IAvailabilityStore
{
    public async Task<IReadOnlyList<AvailabilityWindow>> ListAsync(ulong guildId, ulong userId)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await ForMember(context, guildId, userId).OrderBy(row => row.StartUtc)
            .Select(row => new AvailabilityWindow(row.Id,
                new DateTimeOffset(row.StartUtc, TimeSpan.Zero), new DateTimeOffset(row.EndUtc, TimeSpan.Zero)))
            .ToListAsync();
    }

    public async Task<AvailabilityWindow> SaveAsync(
        ulong guildId, ulong userId, Guid? id, DateTimeOffset start, DateTimeOffset finish)
    {
        if (finish <= start)
            throw new AvailabilityException("End must be after start.");

        await using var context = await factory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var rows = ForMember(context, guildId, userId);
        var existing = id.HasValue ? await rows.SingleOrDefaultAsync(row => row.Id == id.Value) : null;
        if (id.HasValue && existing is null)
            throw new AvailabilityException("Availability window not found.");

        long startUtc = start.UtcTicks;
        long endUtc = finish.UtcTicks;
        if (await rows.AnyAsync(row => (!id.HasValue || row.Id != id.Value) &&
            row.StartUtc < endUtc && startUtc < row.EndUtc))
            throw new AvailabilityException("This overlaps another availability window.");

        var window = existing ?? new AvailabilityRecord
        {
            Id = Guid.NewGuid(),
            GuildId = guildId.ToString(CultureInfo.InvariantCulture),
            UserId = userId.ToString(CultureInfo.InvariantCulture)
        };
        window.StartUtc = startUtc;
        window.EndUtc = endUtc;
        if (existing is null)
            context.Add(window);

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return new AvailabilityWindow(window.Id, start.ToUniversalTime(), finish.ToUniversalTime());
    }

    public async Task<bool> RemoveAsync(ulong guildId, ulong userId, Guid id)
    {
        await using var context = await factory.CreateDbContextAsync();
        return await ForMember(context, guildId, userId).Where(row => row.Id == id).ExecuteDeleteAsync() > 0;
    }

    private static IQueryable<AvailabilityRecord> ForMember(HappyMixDbContext context, ulong guildId, ulong userId)
    {
        string guild = guildId.ToString(CultureInfo.InvariantCulture);
        string user = userId.ToString(CultureInfo.InvariantCulture);
        return context.Set<AvailabilityRecord>().Where(row => row.GuildId == guild && row.UserId == user);
    }
}

internal sealed class AvailabilityRecord
{
    public Guid Id { get; init; }
    public required string GuildId { get; init; }
    public required string UserId { get; init; }
    public long StartUtc { get; set; }
    public long EndUtc { get; set; }
}
