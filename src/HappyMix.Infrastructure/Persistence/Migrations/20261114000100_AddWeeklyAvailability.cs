using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261114000100_AddWeeklyAvailability")]
internal sealed class AddWeeklyAvailability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable("WeeklyAvailability", columns: table => new
        {
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            UserId = table.Column<string>(type: "TEXT", nullable: false),
            Day = table.Column<int>(type: "INTEGER", nullable: false),
            StartTicks = table.Column<long>(type: "INTEGER", nullable: false),
            EndTicks = table.Column<long>(type: "INTEGER", nullable: false),
            TimeZone = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_WeeklyAvailability", row => new { row.GuildId, row.UserId, row.Day }));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("WeeklyAvailability");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        AddAvailabilityWindows.BuildModel(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.WeeklyAvailabilityRecord", entity =>
        {
            entity.Property<string>("GuildId").HasColumnType("TEXT");
            entity.Property<string>("UserId").HasColumnType("TEXT");
            entity.Property<int>("Day").HasColumnType("INTEGER");
            entity.Property<long>("StartTicks").HasColumnType("INTEGER");
            entity.Property<long>("EndTicks").HasColumnType("INTEGER");
            entity.Property<string>("TimeZone").IsRequired().HasColumnType("TEXT");
            entity.HasKey("GuildId", "UserId", "Day");
            entity.ToTable("WeeklyAvailability");
        });
    }
}
