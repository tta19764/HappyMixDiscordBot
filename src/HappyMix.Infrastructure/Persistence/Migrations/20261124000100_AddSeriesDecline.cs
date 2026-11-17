using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>Lets an invitee decline every date of a repeating event at once, so later dates send no more invitations.</summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261124000100_AddSeriesDecline")]
internal sealed class AddSeriesDecline : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        AddNotificationRouteOptions.BuildModel(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.EventInviteeRecord")
            .Property<long?>("SeriesDeclinedUtc").HasColumnType("INTEGER");
    }

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<long>("SeriesDeclinedUtc", "EventInvitees", type: "INTEGER", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE EventInvitees DROP COLUMN SeriesDeclinedUtc");
}
