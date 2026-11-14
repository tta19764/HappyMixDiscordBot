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
        var availability = modelBuilder.Entity<AvailabilityRecord>();
        availability.ToTable("AvailabilityWindows");
        availability.HasKey(item => item.Id);
        availability.HasIndex(item => new { item.GuildId, item.UserId });

        var weekly = modelBuilder.Entity<WeeklyAvailabilityRecord>();
        weekly.ToTable("WeeklyAvailability");
        weekly.HasKey(item => new { item.GuildId, item.UserId, item.Day, item.StartTicks, item.TimeZone });

        var groups = modelBuilder.Entity<GuildGroupRecord>();
        groups.ToTable("GuildGroups");
        groups.HasKey(item => new { item.GuildId, item.NameKey });
        groups.Property(item => item.GuildId).HasColumnType("TEXT");
        groups.Property(item => item.NameKey).HasColumnType("TEXT");
        groups.Property(item => item.Data).HasColumnType("TEXT");
        groups.Property(item => item.GroupId).HasColumnType("TEXT");
        groups.HasIndex(item => item.GroupId).IsUnique();

        var games = modelBuilder.Entity<GuildGroupGameRecord>();
        games.ToTable("GuildGroupGames");
        games.HasKey(item => item.Id);
        games.Property(item => item.Id).HasColumnType("TEXT");
        games.Property(item => item.GuildId).HasColumnType("TEXT");
        games.Property(item => item.GroupId).HasColumnType("TEXT");
        games.Property(item => item.Name).HasColumnType("TEXT");
        games.Property(item => item.Provider).HasColumnType("TEXT");
        games.Property(item => item.ExternalId).HasColumnType("TEXT");
        games.Property(item => item.AddedAtUtc).HasColumnType("TEXT");
        games.HasIndex(item => new { item.GuildId, item.GroupId });
        games.HasIndex(item => new { item.GroupId, item.Provider, item.ExternalId }).IsUnique();

        var links = modelBuilder.Entity<AccountLinkRecord>();
        links.ToTable("AccountLinks");
        links.HasKey(item => new { item.GuildId, item.AlternateId });
        links.Property(item => item.GuildId).HasColumnType("TEXT");
        links.Property(item => item.AlternateId).HasColumnType("TEXT");
        links.Property(item => item.PrimaryId).HasColumnType("TEXT");
        links.Property(item => item.RequestId).HasColumnType("TEXT");
        links.Property(item => item.LinkedUtc).HasColumnType("INTEGER");
        links.Property(item => item.Snapshot).HasColumnType("TEXT");
        links.HasIndex(item => new { item.GuildId, item.PrimaryId });

        var requests = modelBuilder.Entity<AccountLinkRequestRecord>();
        requests.ToTable("AccountLinkRequests");
        requests.HasKey(item => item.Id);
        requests.Property(item => item.Id).HasColumnType("TEXT");
        requests.Property(item => item.GuildId).HasColumnType("TEXT");
        requests.Property(item => item.PrimaryId).HasColumnType("TEXT");
        requests.Property(item => item.TokenHash).HasColumnType("BLOB");
        requests.Property(item => item.CreatedUtc).HasColumnType("INTEGER");
        requests.Property(item => item.ExpiresUtc).HasColumnType("INTEGER");
        requests.Property(item => item.ConsumedUtc).HasColumnType("INTEGER");
        requests.Property(item => item.ConsumedBy).HasColumnType("TEXT");
        requests.Property(item => item.Revoked).HasColumnType("INTEGER");
        requests.HasIndex(item => new { item.GuildId, item.PrimaryId });

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
