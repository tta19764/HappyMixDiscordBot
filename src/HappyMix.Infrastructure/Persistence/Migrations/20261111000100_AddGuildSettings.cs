using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261111000100_AddGuildSettings")]
internal sealed class AddGuildSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable("GuildSettings", columns: table => new
        {
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            ChannelId = table.Column<string>(type: "TEXT", nullable: false),
            TimeZoneId = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_GuildSettings", row => row.GuildId));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("GuildSettings");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => GuildSettingsModel.Build(modelBuilder);
}

internal static class GuildSettingsModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        InitialAnnouncementModel.Build(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.GuildSettingsRecord", entity =>
        {
            entity.Property<string>("GuildId").HasColumnType("TEXT");
            entity.Property<string>("ChannelId").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("TimeZoneId").IsRequired().HasColumnType("TEXT");
            entity.HasKey("GuildId");
            entity.ToTable("GuildSettings");
        });
    }
}
