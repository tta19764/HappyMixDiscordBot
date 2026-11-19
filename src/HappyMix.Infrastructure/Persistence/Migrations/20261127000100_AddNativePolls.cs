using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>Marks votes posted as Discord's own polls, whose votes are copied from Discord.</summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261127000100_AddNativePolls")]
internal sealed class AddNativePolls : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE Polls ADD COLUMN Native INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX IX_Polls_Message ON Polls (MessageId);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP INDEX IX_Polls_Message;
        ALTER TABLE Polls DROP COLUMN Native;
        """);
}
