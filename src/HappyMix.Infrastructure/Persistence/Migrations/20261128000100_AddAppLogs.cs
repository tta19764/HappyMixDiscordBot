using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>Stored log entries for the admin Logs tab. A plain table used through SQL; the EF model is unchanged.</summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261128000100_AddAppLogs")]
internal sealed class AddAppLogs : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE AppLogs (Id INTEGER PRIMARY KEY AUTOINCREMENT, AtMs INTEGER NOT NULL, Level INTEGER NOT NULL, Area TEXT NOT NULL,
            Category TEXT NOT NULL, Message TEXT NOT NULL, Exception TEXT NULL, GuildId TEXT NULL, UserId TEXT NULL, Properties TEXT NULL);
        CREATE INDEX IX_AppLogs_Guild ON AppLogs (GuildId, Id);
        CREATE INDEX IX_AppLogs_At ON AppLogs (AtMs);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE AppLogs;");
}
