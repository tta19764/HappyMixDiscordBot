using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261111000200_AddGuildPreferences")]
internal sealed class AddGuildPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("Preferences", "GuildSettings", type: "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("Preferences", "GuildSettings");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => GuildPreferencesModel.Build(modelBuilder);
}

internal static class GuildPreferencesModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        GuildSettingsModel.Build(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.GuildSettingsRecord")
            .Property<string>("Preferences").HasColumnType("TEXT");
    }
}
