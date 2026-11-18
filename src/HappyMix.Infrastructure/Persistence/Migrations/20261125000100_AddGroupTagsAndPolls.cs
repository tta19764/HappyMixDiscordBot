using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>Group tags and polls. Plain tables used through SQL by SqlitePollStore; the EF model is unchanged.</summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261125000100_AddGroupTagsAndPolls")]
internal sealed class AddGroupTagsAndPolls : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE GroupTags (GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, Tag TEXT NOT NULL, Position INTEGER NOT NULL,
            PRIMARY KEY (GuildId, GroupId, Tag));
        CREATE TABLE Polls (Id TEXT PRIMARY KEY NOT NULL, GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, CreatorId TEXT NOT NULL,
            Question TEXT NOT NULL, Multiple INTEGER NOT NULL, Everyone INTEGER NOT NULL,
            CreatedUtc INTEGER NOT NULL, ClosesUtc INTEGER NOT NULL, ClosedUtc INTEGER NULL);
        CREATE INDEX IX_Polls_Guild_Group ON Polls (GuildId, GroupId, ClosesUtc);
        CREATE TABLE PollOptions (PollId TEXT NOT NULL, Position INTEGER NOT NULL, Label TEXT NOT NULL, Kind TEXT NOT NULL, Url TEXT NULL,
            PRIMARY KEY (PollId, Position), FOREIGN KEY (PollId) REFERENCES Polls (Id) ON DELETE CASCADE);
        CREATE TABLE PollAudience (PollId TEXT NOT NULL, UserId TEXT NOT NULL,
            PRIMARY KEY (PollId, UserId), FOREIGN KEY (PollId) REFERENCES Polls (Id) ON DELETE CASCADE);
        CREATE TABLE PollVotes (PollId TEXT NOT NULL, UserId TEXT NOT NULL, Position INTEGER NOT NULL,
            PRIMARY KEY (PollId, UserId, Position), FOREIGN KEY (PollId) REFERENCES Polls (Id) ON DELETE CASCADE);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE PollVotes; DROP TABLE PollAudience; DROP TABLE PollOptions; DROP TABLE Polls; DROP TABLE GroupTags;
        """);
}
