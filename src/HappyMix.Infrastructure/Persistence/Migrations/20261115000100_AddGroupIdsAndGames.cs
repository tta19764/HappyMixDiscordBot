using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Gives every saved group a durable ID and adds the games attached to groups.
/// Existing names, members, invitations, descriptions and Discord role IDs stay in the untouched JSON document.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261115000100_AddGroupIdsAndGames")]
internal sealed class AddGroupIdsAndGames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("GroupId", "GuildGroups", type: "TEXT", nullable: true);

        // One random version-4 GUID per row, upper case to match how EF Core stores Guid values in SQLite.
        migrationBuilder.Sql("""
            UPDATE GuildGroups SET GroupId = upper(
                hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' ||
                substr('89AB', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))
            WHERE GroupId IS NULL;
            """);
        migrationBuilder.CreateIndex("IX_GuildGroups_GroupId", "GuildGroups", "GroupId", unique: true);

        migrationBuilder.CreateTable("GuildGroupGames", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            GuildId = table.Column<string>(type: "TEXT", nullable: false),
            GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
            Name = table.Column<string>(type: "TEXT", nullable: false),
            Provider = table.Column<string>(type: "TEXT", nullable: true),
            ExternalId = table.Column<string>(type: "TEXT", nullable: true),
            AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_GuildGroupGames", row => row.Id));
        migrationBuilder.CreateIndex("IX_GuildGroupGames_GuildId_GroupId", "GuildGroupGames", ["GuildId", "GroupId"]);
        migrationBuilder.CreateIndex("IX_GuildGroupGames_GroupId_Provider_ExternalId", "GuildGroupGames",
            ["GroupId", "Provider", "ExternalId"], unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("GuildGroupGames");
        migrationBuilder.DropIndex("IX_GuildGroups_GroupId", "GuildGroups");
        migrationBuilder.DropColumn("GroupId", "GuildGroups");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildModel(modelBuilder);

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        AddWeeklyAvailability.BuildModel(modelBuilder);
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.GuildGroupRecord", entity =>
        {
            entity.Property<Guid?>("GroupId").HasColumnType("TEXT");
            entity.HasIndex("GroupId").IsUnique();
        });
        modelBuilder.Entity("HappyMix.Infrastructure.Persistence.GuildGroupGameRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<string>("GuildId").IsRequired().HasColumnType("TEXT");
            entity.Property<Guid>("GroupId").HasColumnType("TEXT");
            entity.Property<string>("Name").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("Provider").HasColumnType("TEXT");
            entity.Property<string>("ExternalId").HasColumnType("TEXT");
            entity.Property<DateTime>("AddedAtUtc").HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("GuildId", "GroupId");
            entity.HasIndex("GroupId", "Provider", "ExternalId").IsUnique();
            entity.ToTable("GuildGroupGames");
        });
    }
}
