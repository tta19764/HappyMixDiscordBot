using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>Weekly digest: the organizer's switch per group and which weeks were already posted. Plain tables used through SQL.</summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261205000100_AddWeeklyDigest")]
internal sealed class AddWeeklyDigest : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE DigestChannels (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, PostDigest INTEGER NOT NULL, PRIMARY KEY (GuildId, GroupId));
        CREATE TABLE GroupDigests (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, WeekStart TEXT NOT NULL, PRIMARY KEY (GuildId, GroupId, WeekStart));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE GroupDigests; DROP TABLE DigestChannels;");
}
