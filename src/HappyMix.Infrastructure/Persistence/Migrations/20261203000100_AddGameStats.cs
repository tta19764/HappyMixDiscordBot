using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Dota 2 and CS2 stats cards: who shares their stats with their groups (off by default) and the last fetched card per
/// person and game. Plain tables used through SQL; the EF model is unchanged.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261203000100_AddGameStats")]
internal sealed class AddGameStats : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE GameStatsSharing (UserId TEXT NOT NULL PRIMARY KEY, Share INTEGER NOT NULL, ChangedUtc INTEGER NOT NULL);
        CREATE TABLE GameStatsCache (UserId TEXT NOT NULL, Game TEXT NOT NULL, Status TEXT NOT NULL, Payload TEXT NULL, FetchedUtc INTEGER NOT NULL,
            PRIMARY KEY (UserId, Game));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE GameStatsCache; DROP TABLE GameStatsSharing;");
}
