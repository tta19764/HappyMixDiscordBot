using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261112000100_AddGuildGroups")]
internal sealed class AddGuildGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable("GuildGroups", columns: table => new
        {
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            NameKey = table.Column<string>(type: "TEXT", nullable: false),
            Data = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_GuildGroups", row => new { row.GuildId, row.NameKey }));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("GuildGroups");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => GuildGroupsModel.Build(modelBuilder);
}

internal static class GuildGroupsModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        GuildPreferencesModel.Build(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.GuildGroupRecord", entity =>
        {
            entity.Property<string>("GuildId").HasColumnType("TEXT");
            entity.Property<string>("NameKey").HasColumnType("TEXT");
            entity.Property<string>("Data").IsRequired().HasColumnType("TEXT");
            entity.HasKey("GuildId", "NameKey");
            entity.ToTable("GuildGroups");
        });
    }
}
