using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Per-route reminder lead time (null uses the deployment default) and an opt-in for channel updates when events are
/// scheduled, confirmed or canceled. Existing routes keep their reminder-only behavior.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261123000100_AddNotificationRouteOptions")]
internal sealed class AddNotificationRouteOptions : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    // NotificationRoutes is not an EF entity, so the frozen model is unchanged.
    internal static void BuildModel(ModelBuilder modelBuilder) => AddSeriesParticipation.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE NotificationRoutes ADD COLUMN ReminderMinutes INTEGER NULL;
        ALTER TABLE NotificationRoutes ADD COLUMN Updates INTEGER NOT NULL DEFAULT 0;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE NotificationRoutes DROP COLUMN Updates;
        ALTER TABLE NotificationRoutes DROP COLUMN ReminderMinutes;
        """);
}
