using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261118000100_AddAvailabilityRanges")]
internal sealed class AddAvailabilityRanges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("Tentative", "AvailabilityWindows", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.Sql("""
            CREATE TABLE WeeklyAvailability_new (
                GuildId TEXT NOT NULL, UserId TEXT NOT NULL, Day INTEGER NOT NULL,
                StartTicks INTEGER NOT NULL, EndTicks INTEGER NOT NULL, TimeZone TEXT NOT NULL,
                Tentative INTEGER NOT NULL,
                CONSTRAINT PK_WeeklyAvailability PRIMARY KEY (GuildId, UserId, Day, StartTicks, TimeZone));
            INSERT INTO WeeklyAvailability_new
                SELECT GuildId, UserId, Day, StartTicks, EndTicks, TimeZone, 0 FROM WeeklyAvailability;
            DROP TABLE WeeklyAvailability;
            ALTER TABLE WeeklyAvailability_new RENAME TO WeeklyAvailability;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Multiple weekly ranges cannot be downgraded without losing availability. Restore a backup instead.");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        AddAccountLinks.BuildModel(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.AvailabilityRecord", entity =>
            entity.Property<bool>("Tentative").HasColumnType("INTEGER"));
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.WeeklyAvailabilityRecord", entity =>
        {
            entity.Property<bool>("Tentative").HasColumnType("INTEGER");
            entity.HasKey("GuildId", "UserId", "Day", "StartTicks", "TimeZone");
        });
    }
}
