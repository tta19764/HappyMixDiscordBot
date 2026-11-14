using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Additive: linked alternate accounts and their single-use link requests. No existing table or row changes.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261116000100_AddAccountLinks")]
internal sealed class AddAccountLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("AccountLinks", columns: table => new
        {
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            AlternateId = table.Column<string>(type: "TEXT", nullable: false),
            PrimaryId = table.Column<string>(type: "TEXT", nullable: false),
            RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
            LinkedUtc = table.Column<long>(type: "INTEGER", nullable: false),
            Snapshot = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_AccountLinks", row => new { row.GuildId, row.AlternateId }));
        migrationBuilder.CreateIndex("IX_AccountLinks_GuildId_PrimaryId", "AccountLinks", ["GuildId", "PrimaryId"]);

        migrationBuilder.CreateTable("AccountLinkRequests", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            PrimaryId = table.Column<string>(type: "TEXT", nullable: false),
            TokenHash = table.Column<byte[]>(type: "BLOB", nullable: false),
            CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
            ExpiresUtc = table.Column<long>(type: "INTEGER", nullable: false),
            ConsumedUtc = table.Column<long>(type: "INTEGER", nullable: true),
            ConsumedBy = table.Column<string>(type: "TEXT", nullable: true),
            Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_AccountLinkRequests", row => row.Id));
        migrationBuilder.CreateIndex("IX_AccountLinkRequests_GuildId_PrimaryId", "AccountLinkRequests", ["GuildId", "PrimaryId"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("AccountLinkRequests");
        migrationBuilder.DropTable("AccountLinks");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        AddGroupIdsAndGames.BuildModel(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.AccountLinkRecord", entity =>
        {
            entity.Property<string>("GuildId").HasColumnType("TEXT");
            entity.Property<string>("AlternateId").HasColumnType("TEXT");
            entity.Property<string>("PrimaryId").IsRequired().HasColumnType("TEXT");
            entity.Property<Guid>("RequestId").HasColumnType("TEXT");
            entity.Property<long>("LinkedUtc").HasColumnType("INTEGER");
            entity.Property<string>("Snapshot").IsRequired().HasColumnType("TEXT");
            entity.HasKey("GuildId", "AlternateId");
            entity.HasIndex("GuildId", "PrimaryId");
            entity.ToTable("AccountLinks");
        });
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.AccountLinkRequestRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<string>("GuildId").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("PrimaryId").IsRequired().HasColumnType("TEXT");
            entity.Property<byte[]>("TokenHash").IsRequired().HasColumnType("BLOB");
            entity.Property<long>("CreatedUtc").HasColumnType("INTEGER");
            entity.Property<long>("ExpiresUtc").HasColumnType("INTEGER");
            entity.Property<long?>("ConsumedUtc").HasColumnType("INTEGER");
            entity.Property<string>("ConsumedBy").HasColumnType("TEXT");
            entity.Property<bool>("Revoked").HasColumnType("INTEGER");
            entity.HasKey("Id");
            entity.HasIndex("GuildId", "PrimaryId");
            entity.ToTable("AccountLinkRequests");
        });
    }
}
