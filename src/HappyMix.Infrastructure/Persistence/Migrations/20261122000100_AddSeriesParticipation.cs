using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261122000100_AddSeriesParticipation")]
internal sealed class AddSeriesParticipation : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        AddEvents.BuildModel(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.EventInviteeRecord")
            .Property<long?>("SeriesAcceptedUtc").HasColumnType("INTEGER");
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>("SeriesAcceptedUtc", "EventInvitees", type: "INTEGER", nullable: true);
        migrationBuilder.Sql("""
            UPDATE EventInvitees SET SeriesAcceptedUtc = (
                SELECT MIN(r.RespondedUtc) FROM EventResponses r
                JOIN Events e ON e.Id = r.EventId
                WHERE r.EventId = EventInvitees.EventId AND r.UserId = EventInvitees.UserId
                  AND r.Accepted = 1 AND e.Recurrence <> 0);
            CREATE TABLE NotificationRoutes (GuildId TEXT NOT NULL, Scope TEXT NOT NULL, TargetId TEXT NOT NULL,
                Enabled INTEGER NOT NULL, ChannelId TEXT NOT NULL, Language TEXT NOT NULL,
                PRIMARY KEY (GuildId, Scope, TargetId));
            CREATE TABLE EventChannelDelivery (DeliveryKey TEXT PRIMARY KEY NOT NULL);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("EventChannelDelivery");
        migrationBuilder.DropTable("NotificationRoutes");
        migrationBuilder.Sql("ALTER TABLE EventInvitees DROP COLUMN SeriesAcceptedUtc");
    }
}
