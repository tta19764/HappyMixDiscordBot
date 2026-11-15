using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Additive: scheduled events, their invitees, one row per date, answers per date, and the outbox of Discord messages.
/// No existing table or row changes.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261120000100_AddEvents")]
internal sealed class AddEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Events", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            OrganizerId = table.Column<string>(type: "TEXT", nullable: false),
            GroupId = table.Column<Guid>(type: "TEXT", nullable: true),
            Title = table.Column<string>(type: "TEXT", nullable: false),
            Description = table.Column<string>(type: "TEXT", nullable: true),
            TimeZone = table.Column<string>(type: "TEXT", nullable: false),
            StartDate = table.Column<string>(type: "TEXT", nullable: false),
            StartMinute = table.Column<int>(type: "INTEGER", nullable: false),
            DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
            Recurrence = table.Column<int>(type: "INTEGER", nullable: false),
            Weekdays = table.Column<int>(type: "INTEGER", nullable: false),
            UntilDate = table.Column<string>(type: "TEXT", nullable: true),
            CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
            CanceledUtc = table.Column<long>(type: "INTEGER", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_Events", row => row.Id));
        migrationBuilder.CreateIndex("IX_Events_GuildId_OrganizerId", "Events", ["GuildId", "OrganizerId"]);
        migrationBuilder.CreateIndex("IX_Events_GuildId_GroupId", "Events", ["GuildId", "GroupId"]);

        migrationBuilder.CreateTable("EventInvitees", columns: table => new
        {
            EventId = table.Column<Guid>(type: "TEXT", nullable: false),
            UserId = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_EventInvitees", row => new { row.EventId, row.UserId }));
        migrationBuilder.CreateIndex("IX_EventInvitees_UserId", "EventInvitees", "UserId");

        migrationBuilder.CreateTable("EventOccurrences", columns: table => new
        {
            EventId = table.Column<Guid>(type: "TEXT", nullable: false),
            Date = table.Column<string>(type: "TEXT", nullable: false),
            StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
            EndUtc = table.Column<long>(type: "INTEGER", nullable: false),
            Status = table.Column<int>(type: "INTEGER", nullable: false),
            ConfirmedUtc = table.Column<long>(type: "INTEGER", nullable: true),
            CanceledUtc = table.Column<long>(type: "INTEGER", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_EventOccurrences", row => new { row.EventId, row.Date }));
        migrationBuilder.CreateIndex("IX_EventOccurrences_StartUtc", "EventOccurrences", "StartUtc");

        migrationBuilder.CreateTable("EventResponses", columns: table => new
        {
            EventId = table.Column<Guid>(type: "TEXT", nullable: false),
            Date = table.Column<string>(type: "TEXT", nullable: false),
            UserId = table.Column<string>(type: "TEXT", nullable: false),
            Accepted = table.Column<bool>(type: "INTEGER", nullable: false),
            Withdrawn = table.Column<bool>(type: "INTEGER", nullable: false),
            RespondedUtc = table.Column<long>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_EventResponses", row => new { row.EventId, row.Date, row.UserId }));

        migrationBuilder.CreateTable("EventNotifications", columns: table => new
        {
            EventId = table.Column<Guid>(type: "TEXT", nullable: false),
            Date = table.Column<string>(type: "TEXT", nullable: false),
            UserId = table.Column<string>(type: "TEXT", nullable: false),
            Kind = table.Column<int>(type: "INTEGER", nullable: false),
            CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
            NextAttemptUtc = table.Column<long>(type: "INTEGER", nullable: false),
            Attempts = table.Column<int>(type: "INTEGER", nullable: false),
            Sent = table.Column<bool>(type: "INTEGER", nullable: false),
            Failed = table.Column<bool>(type: "INTEGER", nullable: false),
            Error = table.Column<string>(type: "TEXT", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_EventNotifications", row => new { row.EventId, row.Date, row.UserId, row.Kind }));
        migrationBuilder.CreateIndex("IX_EventNotifications_Sent_Failed_NextAttemptUtc", "EventNotifications", ["Sent", "Failed", "NextAttemptUtc"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("EventNotifications");
        migrationBuilder.DropTable("EventResponses");
        migrationBuilder.DropTable("EventOccurrences");
        migrationBuilder.DropTable("EventInvitees");
        migrationBuilder.DropTable("Events");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        AddAvailabilityRanges.BuildModel(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.EventRecord", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT");
            entity.Property<string>("GuildId").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("OrganizerId").IsRequired().HasColumnType("TEXT");
            entity.Property<Guid?>("GroupId").HasColumnType("TEXT");
            entity.Property<string>("Title").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("Description").HasColumnType("TEXT");
            entity.Property<string>("TimeZone").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("StartDate").IsRequired().HasColumnType("TEXT");
            entity.Property<int>("StartMinute").HasColumnType("INTEGER");
            entity.Property<int>("DurationMinutes").HasColumnType("INTEGER");
            entity.Property<int>("Recurrence").HasColumnType("INTEGER");
            entity.Property<int>("Weekdays").HasColumnType("INTEGER");
            entity.Property<string>("UntilDate").HasColumnType("TEXT");
            entity.Property<long>("CreatedUtc").HasColumnType("INTEGER");
            entity.Property<long?>("CanceledUtc").HasColumnType("INTEGER");
            entity.HasKey("Id");
            entity.HasIndex("GuildId", "OrganizerId");
            entity.HasIndex("GuildId", "GroupId");
            entity.ToTable("Events");
        });
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.EventInviteeRecord", entity =>
        {
            entity.Property<Guid>("EventId").HasColumnType("TEXT");
            entity.Property<string>("UserId").IsRequired().HasColumnType("TEXT");
            entity.HasKey("EventId", "UserId");
            entity.HasIndex("UserId");
            entity.ToTable("EventInvitees");
        });
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.EventOccurrenceRecord", entity =>
        {
            entity.Property<Guid>("EventId").HasColumnType("TEXT");
            entity.Property<string>("Date").IsRequired().HasColumnType("TEXT");
            entity.Property<long>("StartUtc").HasColumnType("INTEGER");
            entity.Property<long>("EndUtc").HasColumnType("INTEGER");
            entity.Property<int>("Status").HasColumnType("INTEGER");
            entity.Property<long?>("ConfirmedUtc").HasColumnType("INTEGER");
            entity.Property<long?>("CanceledUtc").HasColumnType("INTEGER");
            entity.HasKey("EventId", "Date");
            entity.HasIndex("StartUtc");
            entity.ToTable("EventOccurrences");
        });
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.EventResponseRecord", entity =>
        {
            entity.Property<Guid>("EventId").HasColumnType("TEXT");
            entity.Property<string>("Date").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("UserId").IsRequired().HasColumnType("TEXT");
            entity.Property<bool>("Accepted").HasColumnType("INTEGER");
            entity.Property<bool>("Withdrawn").HasColumnType("INTEGER");
            entity.Property<long>("RespondedUtc").HasColumnType("INTEGER");
            entity.HasKey("EventId", "Date", "UserId");
            entity.ToTable("EventResponses");
        });
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.EventNotificationRecord", entity =>
        {
            entity.Property<Guid>("EventId").HasColumnType("TEXT");
            entity.Property<string>("Date").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("UserId").IsRequired().HasColumnType("TEXT");
            entity.Property<int>("Kind").HasColumnType("INTEGER");
            entity.Property<long>("CreatedUtc").HasColumnType("INTEGER");
            entity.Property<long>("NextAttemptUtc").HasColumnType("INTEGER");
            entity.Property<int>("Attempts").HasColumnType("INTEGER");
            entity.Property<bool>("Sent").HasColumnType("INTEGER");
            entity.Property<bool>("Failed").HasColumnType("INTEGER");
            entity.Property<string>("Error").HasColumnType("TEXT");
            entity.HasKey("EventId", "Date", "UserId", "Kind");
            entity.HasIndex("Sent", "Failed", "NextAttemptUtc");
            entity.ToTable("EventNotifications");
        });
    }
}
