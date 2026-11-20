using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Players' Steam links and game lists with their marks (1 want, 2 can, 3 won't). Plain tables used through SQL; the EF
/// model is unchanged. Data belongs to the Discord account, not to a server.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261130000100_AddPlayerGames")]
internal sealed class AddPlayerGames : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE SteamAccounts (UserId TEXT NOT NULL PRIMARY KEY, SteamId TEXT NOT NULL, Verified INTEGER NOT NULL,
            LinkedUtc INTEGER NOT NULL, ImportedUtc INTEGER NULL);
        CREATE UNIQUE INDEX IX_SteamAccounts_SteamId ON SteamAccounts (SteamId);
        CREATE TABLE PlayerGames (UserId TEXT NOT NULL, GameKey TEXT NOT NULL, Name TEXT NOT NULL, Provider TEXT NULL, ExternalId TEXT NULL,
            Preference INTEGER NOT NULL, FromSteam INTEGER NOT NULL, PlaytimeMinutes INTEGER NULL, RecentMinutes INTEGER NULL,
            UpdatedUtc INTEGER NOT NULL, PRIMARY KEY (UserId, GameKey));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE PlayerGames; DROP TABLE SteamAccounts;");
}
