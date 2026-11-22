using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Game features: why the last stats lookup failed (shown on the card), the Dota 2 meta schedule per server, and a
/// shared cache for data every server uses. Plain tables used through SQL.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261205000200_AddGameFeatures")]
internal sealed class AddGameFeatures : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE GameStatsCache ADD COLUMN Reason TEXT NULL;
        CREATE TABLE DotaMetaSettings (GuildId TEXT NOT NULL PRIMARY KEY, Schedule TEXT NOT NULL, ChannelId TEXT NULL, Day INTEGER NOT NULL,
            Hour INTEGER NOT NULL, LastPostedUtc INTEGER NULL, TopCount INTEGER NOT NULL DEFAULT 10, MetaRank TEXT NOT NULL DEFAULT 'All',
            MetaPatch TEXT NOT NULL DEFAULT 'Current', MetaSort TEXT NOT NULL DEFAULT 'Score');
        CREATE TABLE FeatureCache (Key TEXT NOT NULL PRIMARY KEY, Payload TEXT NOT NULL, FetchedUtc INTEGER NOT NULL);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE FeatureCache; DROP TABLE DotaMetaSettings; ALTER TABLE GameStatsCache DROP COLUMN Reason;");
}
