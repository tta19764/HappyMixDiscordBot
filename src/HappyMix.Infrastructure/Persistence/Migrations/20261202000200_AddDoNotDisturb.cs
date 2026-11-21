using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Do-not-disturb: weekly hours and dated stretches per Discord account, and personal messages held until they end.
/// Plain tables used through SQL; the EF model is unchanged.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261202000200_AddDoNotDisturb")]
internal sealed class AddDoNotDisturb : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE QuietWeekly (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, Day INTEGER NOT NULL, StartMinute INTEGER NOT NULL,
            EndMinute INTEGER NOT NULL, TimeZone TEXT NOT NULL);
        CREATE INDEX IX_QuietWeekly_User ON QuietWeekly (UserId);
        CREATE TABLE QuietDates (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, StartUtc INTEGER NOT NULL, EndUtc INTEGER NOT NULL);
        CREATE INDEX IX_QuietDates_User ON QuietDates (UserId);
        CREATE TABLE HeldMessages (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Kind TEXT NOT NULL, Key TEXT NULL,
            Payload TEXT NOT NULL, HeldUtc INTEGER NOT NULL, ReleaseUtc INTEGER NOT NULL);
        CREATE INDEX IX_HeldMessages_Release ON HeldMessages (ReleaseUtc);
        CREATE INDEX IX_HeldMessages_User ON HeldMessages (UserId, Kind, Key);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE HeldMessages; DROP TABLE QuietDates; DROP TABLE QuietWeekly;");
}
