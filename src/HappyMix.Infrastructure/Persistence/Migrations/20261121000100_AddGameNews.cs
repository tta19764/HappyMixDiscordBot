using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HappyMixDbContext))]
[Migration("20261121000100_AddGameNews")]
internal sealed class AddGameNews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE GameNews (
                GuildId TEXT NOT NULL, AppId INTEGER NOT NULL, Name TEXT NOT NULL,
                OwnerId TEXT NOT NULL, ChannelId TEXT NOT NULL, RoleId TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL, Deleting INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (GuildId, AppId));
            CREATE TABLE GameNewsDelivery (
                GuildId TEXT NOT NULL, AppId INTEGER NOT NULL, NewsId TEXT NOT NULL,
                PRIMARY KEY (GuildId, AppId, NewsId),
                FOREIGN KEY (GuildId, AppId) REFERENCES GameNews (GuildId, AppId) ON DELETE CASCADE);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("GameNewsDelivery");
        migrationBuilder.DropTable("GameNews");
    }
}
