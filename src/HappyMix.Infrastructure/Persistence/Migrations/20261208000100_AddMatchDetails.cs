using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Whole matches played together: both teams and every player, read once per match and kept as long as the match itself.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261208000100_AddMatchDetails")]
internal sealed class AddMatchDetails : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE IF NOT EXISTS MatchDetails (
            Game TEXT NOT NULL,
            MatchId TEXT NOT NULL,
            StartedUtc INTEGER NOT NULL,
            Payload TEXT NOT NULL,
            FetchedUtc INTEGER NOT NULL,
            PRIMARY KEY (Game, MatchId)
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS MatchDetails;");
}
