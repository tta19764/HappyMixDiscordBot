using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Twink accounts: a Discord account can link several Steam accounts. One is the main account (IsMain); each one says
/// whether its stats are shown (ShowStats) and can carry a label. Stats cards are kept per Steam account, so the cache
/// key gains the SteamId; existing cards belong to the account linked so far.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261207000200_AddSteamTwinks")]
internal sealed class AddSteamTwinks : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE SteamAccountsNew (UserId TEXT NOT NULL, SteamId TEXT NOT NULL, Verified INTEGER NOT NULL, LinkedUtc INTEGER NOT NULL,
            ImportedUtc INTEGER NULL, IsMain INTEGER NOT NULL DEFAULT 1, ShowStats INTEGER NOT NULL DEFAULT 1, Label TEXT NULL,
            PRIMARY KEY (UserId, SteamId));
        INSERT INTO SteamAccountsNew (UserId, SteamId, Verified, LinkedUtc, ImportedUtc)
            SELECT UserId, SteamId, Verified, LinkedUtc, ImportedUtc FROM SteamAccounts;
        DROP TABLE SteamAccounts;
        ALTER TABLE SteamAccountsNew RENAME TO SteamAccounts;
        CREATE UNIQUE INDEX IX_SteamAccounts_SteamId ON SteamAccounts (SteamId);

        CREATE TABLE GameStatsCacheNew (UserId TEXT NOT NULL, SteamId TEXT NOT NULL DEFAULT '', Game TEXT NOT NULL, Status TEXT NOT NULL,
            Payload TEXT NULL, FetchedUtc INTEGER NOT NULL, Reason TEXT NULL, PRIMARY KEY (UserId, SteamId, Game));
        INSERT INTO GameStatsCacheNew (UserId, SteamId, Game, Status, Payload, FetchedUtc, Reason)
            SELECT c.UserId, COALESCE((SELECT s.SteamId FROM SteamAccounts s WHERE s.UserId = c.UserId AND s.IsMain = 1), ''),
                   c.Game, c.Status, c.Payload, c.FetchedUtc, c.Reason
            FROM GameStatsCache c;
        DROP TABLE GameStatsCache;
        ALTER TABLE GameStatsCacheNew RENAME TO GameStatsCache;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE GameStatsCacheOld (UserId TEXT NOT NULL, Game TEXT NOT NULL, Status TEXT NOT NULL, Payload TEXT NULL, FetchedUtc INTEGER NOT NULL,
            Reason TEXT NULL, PRIMARY KEY (UserId, Game));
        INSERT OR IGNORE INTO GameStatsCacheOld SELECT c.UserId, c.Game, c.Status, c.Payload, c.FetchedUtc, c.Reason FROM GameStatsCache c
            WHERE c.SteamId = '' OR c.SteamId IN (SELECT SteamId FROM SteamAccounts WHERE IsMain = 1);
        DROP TABLE GameStatsCache;
        ALTER TABLE GameStatsCacheOld RENAME TO GameStatsCache;
        CREATE TABLE SteamAccountsOld (UserId TEXT NOT NULL PRIMARY KEY, SteamId TEXT NOT NULL, Verified INTEGER NOT NULL,
            LinkedUtc INTEGER NOT NULL, ImportedUtc INTEGER NULL);
        INSERT OR IGNORE INTO SteamAccountsOld SELECT UserId, SteamId, Verified, LinkedUtc, ImportedUtc FROM SteamAccounts WHERE IsMain = 1;
        DROP TABLE SteamAccounts;
        ALTER TABLE SteamAccountsOld RENAME TO SteamAccounts;
        CREATE UNIQUE INDEX IX_SteamAccounts_SteamId ON SteamAccounts (SteamId);
        """);
}
