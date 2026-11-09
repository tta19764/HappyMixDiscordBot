using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>Frozen schema baseline for generating the next migration.</summary>
[DbContext(typeof(HappyMixDbContext))]
internal sealed class HappyMixDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder) => InitialAnnouncementModel.Build(modelBuilder);
}

/// <summary>Immutable initial model, shared only by this migration and its initial snapshot.</summary>
/// <remarks>Future snapshots must contain their new frozen model; do not update this initial model.</remarks>
internal static class InitialAnnouncementModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.AnnouncementRecord", entity =>
        {
            entity.Property<string>("Scope").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("Key").IsRequired().HasColumnType("TEXT");
            entity.Property<DateTime>("RecordedAtUtc").HasColumnType("TEXT");
            entity.HasKey("Scope", "Key");
            entity.ToTable("AnnouncementRecords");
        });
    }
}
