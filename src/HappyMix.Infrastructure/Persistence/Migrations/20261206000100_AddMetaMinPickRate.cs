using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>The Dota 2 meta post's minimum pick rate (percent); heroes picked less often are left out. Defaults to 1%.</summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261206000100_AddMetaMinPickRate")]
internal sealed class AddMetaMinPickRate : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE DotaMetaSettings ADD COLUMN MinPick REAL NOT NULL DEFAULT 1;");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE DotaMetaSettings DROP COLUMN MinPick;");
}
