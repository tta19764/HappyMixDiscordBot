using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Match recaps: recent matches of people who share their stats (kept 30 days), when each person's history was last
/// read, which group recaps were handled, and the organizer's channel switch. Plain tables used through SQL.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261204000100_AddMatchRecaps")]
internal sealed class AddMatchRecaps : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE PlayerMatches (UserId TEXT NOT NULL, Game TEXT NOT NULL, MatchId TEXT NOT NULL, StartedUtc INTEGER NOT NULL, Duration INTEGER NOT NULL,
            Won INTEGER NOT NULL, Pick TEXT NULL, Image TEXT NULL, Kills INTEGER NOT NULL, Deaths INTEGER NOT NULL, Assists INTEGER NOT NULL,
            Headshots REAL NULL, Score TEXT NULL, PRIMARY KEY (UserId, Game, MatchId));
        CREATE INDEX IX_PlayerMatches_Started ON PlayerMatches (StartedUtc);
        CREATE TABLE MatchFetches (UserId TEXT NOT NULL, Game TEXT NOT NULL, FetchedUtc INTEGER NOT NULL, PRIMARY KEY (UserId, Game));
        CREATE TABLE GroupRecaps (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, Game TEXT NOT NULL, MatchId TEXT NOT NULL, HandledUtc INTEGER NOT NULL,
            PRIMARY KEY (GuildId, GroupId, Game, MatchId));
        CREATE TABLE RecapChannels (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, PostInChannel INTEGER NOT NULL, PRIMARY KEY (GuildId, GroupId));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE RecapChannels; DROP TABLE GroupRecaps; DROP TABLE MatchFetches; DROP TABLE PlayerMatches;");
}
