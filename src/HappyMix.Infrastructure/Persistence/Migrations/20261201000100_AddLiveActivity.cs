using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Live: per-group sharing and DM choices, the organizer's channel switch, play sessions (kept 14 days) and the last
/// notice per player and game for the cooldown. Plain tables used through SQL; the EF model is unchanged.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261201000100_AddLiveActivity")]
internal sealed class AddLiveActivity : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE LiveMembers (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, UserId TEXT NOT NULL, Share INTEGER NOT NULL, Notify INTEGER NOT NULL,
            PRIMARY KEY (GuildId, GroupId, UserId));
        CREATE INDEX IX_LiveMembers_User ON LiveMembers (UserId);
        CREATE TABLE LiveGroups (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, PostInChannel INTEGER NOT NULL, PRIMARY KEY (GuildId, GroupId));
        CREATE TABLE PlaySessions (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Game TEXT NOT NULL, Source TEXT NOT NULL,
            StartedUtc INTEGER NOT NULL, EndedUtc INTEGER NULL);
        CREATE INDEX IX_PlaySessions_User ON PlaySessions (UserId, EndedUtc);
        CREATE TABLE LiveAlerts (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, UserId TEXT NOT NULL, Game TEXT NOT NULL, SentUtc INTEGER NOT NULL,
            PRIMARY KEY (GuildId, GroupId, UserId, Game));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE LiveAlerts; DROP TABLE PlaySessions; DROP TABLE LiveGroups; DROP TABLE LiveMembers;");
}
