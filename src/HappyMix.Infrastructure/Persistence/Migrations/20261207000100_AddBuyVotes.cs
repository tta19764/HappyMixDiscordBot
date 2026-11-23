using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// "Will you buy this game" votes: a vote's purpose ('buy', or NULL for a normal vote) and the game it asks about, so
/// members who own it in Steam can be counted as "Already have".
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261207000100_AddBuyVotes")]
internal sealed class AddBuyVotes : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE Polls ADD COLUMN Purpose TEXT NULL;
        ALTER TABLE Polls ADD COLUMN GameKey TEXT NULL;
        ALTER TABLE Polls ADD COLUMN GameName TEXT NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE Polls DROP COLUMN GameName; ALTER TABLE Polls DROP COLUMN GameKey; ALTER TABLE Polls DROP COLUMN Purpose;");
}
