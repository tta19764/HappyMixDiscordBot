using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Which kinds of Steam news each subscription posts (flags: 1 patch notes, 2 announcements, 4 press).
/// Existing subscriptions keep the game's own Steam page posts only.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261129000100_AddGameNewsSources")]
internal sealed class AddGameNewsSources : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE GameNews ADD COLUMN Sources INTEGER NOT NULL DEFAULT 3;");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE GameNews DROP COLUMN Sources;");
}
