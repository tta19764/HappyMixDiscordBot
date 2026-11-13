using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261113000100_AddAvailabilityWindows")]
internal sealed class AddAvailabilityWindows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("AvailabilityWindows", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            UserId = table.Column<string>(type: "TEXT", nullable: false),
            StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
            EndUtc = table.Column<long>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_AvailabilityWindows", row => row.Id));
        migrationBuilder.CreateIndex("IX_AvailabilityWindows_GuildId_UserId", "AvailabilityWindows", ["GuildId", "UserId"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("AvailabilityWindows");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        GuildGroupsModel.Build(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.AvailabilityRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<string>("GuildId").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("UserId").IsRequired().HasColumnType("TEXT");
            entity.Property<long>("StartUtc").HasColumnType("INTEGER");
            entity.Property<long>("EndUtc").HasColumnType("INTEGER");
            entity.HasKey("Id");
            entity.HasIndex("GuildId", "UserId");
            entity.ToTable("AvailabilityWindows");
        });
    }
}
