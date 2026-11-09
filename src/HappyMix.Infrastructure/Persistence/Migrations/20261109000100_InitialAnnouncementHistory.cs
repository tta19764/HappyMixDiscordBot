using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>Creates durable delivery history and per-channel bootstrap markers.</summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261109000100_InitialAnnouncementHistory")]
internal sealed class InitialAnnouncementHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AnnouncementRecords",
            columns: table => new
            {
                Scope = table.Column<string>(type: "TEXT", nullable: false),
                Key = table.Column<string>(type: "TEXT", nullable: false),
                RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AnnouncementRecords", item => new { item.Scope, item.Key }));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("AnnouncementRecords");

    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        InitialAnnouncementModel.Build(modelBuilder);
}
