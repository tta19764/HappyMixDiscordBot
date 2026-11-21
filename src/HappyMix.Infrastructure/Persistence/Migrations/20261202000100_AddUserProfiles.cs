using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Discord profiles saved at dashboard sign-in and "Sync with Discord": name, avatar and the Steam connection. A plain
/// table used through SQL; the EF model is unchanged.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261202000100_AddUserProfiles")]
internal sealed class AddUserProfiles : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE UserProfiles (UserId TEXT NOT NULL PRIMARY KEY, UserName TEXT NOT NULL, DisplayName TEXT NULL, Avatar TEXT NULL,
            SteamId TEXT NULL, SteamName TEXT NULL, SteamVerified INTEGER NOT NULL, ConnectionsChecked INTEGER NOT NULL, SyncedUtc INTEGER NOT NULL);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE UserProfiles;");
}
