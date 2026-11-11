using Microsoft.EntityFrameworkCore;

namespace HappyMix.Infrastructure.Persistence;

/// <summary>SQLite unit of work; each delivery-state operation creates its own instance.</summary>
/// <param name="options">Configured SQLite connection and provider options.</param>
internal sealed class HappyMixDbContext(DbContextOptions<HappyMixDbContext> options) : DbContext(options)
{
    /// <summary>Durable delivery markers and the first-synchronization marker.</summary>
    public DbSet<AnnouncementRecord> Announcements => Set<AnnouncementRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<GuildSettingsRecord>();
        settings.ToTable("GuildSettings");
        settings.HasKey(item => item.GuildId);
        settings.Property(item => item.GuildId).HasColumnType("TEXT");
        settings.Property(item => item.ChannelId).HasColumnType("TEXT");
        settings.Property(item => item.TimeZoneId).HasColumnType("TEXT");
        settings.Property(item => item.Preferences).HasColumnType("TEXT");

        var record = modelBuilder.Entity<AnnouncementRecord>();
        record.ToTable("AnnouncementRecords");
        record.HasKey(item => new { item.Scope, item.Key });
        record.Property(item => item.Scope).HasColumnType("TEXT");
        record.Property(item => item.Key).HasColumnType("TEXT");
        record.Property(item => item.RecordedAtUtc).HasColumnType("TEXT");
    }
}

/// <summary>A unique publication or bootstrap marker scoped to its destination channel.</summary>
internal sealed class AnnouncementRecord
{
    /// <summary>Discord announcement-channel ID, or the default scope for non-Discord callers.</summary>
    public required string Scope { get; init; }
    /// <summary>Event kind plus discipline and provider ID, or local schedule date.</summary>
    public required string Key { get; init; }
    /// <summary>UTC insertion time for diagnosis and future retention policy.</summary>
    public DateTime RecordedAtUtc { get; init; }
}
