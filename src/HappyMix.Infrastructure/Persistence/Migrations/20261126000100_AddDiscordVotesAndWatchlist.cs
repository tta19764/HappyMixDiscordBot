using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HappyMix.Infrastructure.Persistence.Migrations;

/// <summary>
/// Discord messages for votes, catalog references on vote options, a cache of catalog lookups and group watchlists.
/// Plain tables used through SQL; the EF model is unchanged.
/// </summary>
[DbContext(typeof(HappyMixDbContext))]
[Migration("20261126000100_AddDiscordVotesAndWatchlist")]
internal sealed class AddDiscordVotesAndWatchlist : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AddSeriesDecline.BuildModel(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE Polls ADD COLUMN ChannelId TEXT NULL;
        ALTER TABLE Polls ADD COLUMN MessageId TEXT NULL;
        ALTER TABLE Polls ADD COLUMN Finalized INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE PollOptions ADD COLUMN Source TEXT NULL;
        ALTER TABLE PollOptions ADD COLUMN ExternalId TEXT NULL;
        CREATE TABLE MediaCache (Source TEXT NOT NULL, ExternalId TEXT NOT NULL, Json TEXT NOT NULL, FetchedUtc INTEGER NOT NULL,
            PRIMARY KEY (Source, ExternalId));
        CREATE TABLE WatchItems (Id TEXT PRIMARY KEY NOT NULL, GuildId TEXT NOT NULL, GroupId TEXT NOT NULL, Kind TEXT NOT NULL,
            Title TEXT NOT NULL, Url TEXT NULL, ImageUrl TEXT NULL, Source TEXT NULL, ExternalId TEXT NULL,
            Status INTEGER NOT NULL, Season INTEGER NOT NULL, Episode INTEGER NOT NULL, PositionSeconds INTEGER NULL,
            AddedBy TEXT NOT NULL, AddedUtc INTEGER NOT NULL, UpdatedUtc INTEGER NOT NULL, FinishedUtc INTEGER NULL);
        CREATE INDEX IX_WatchItems_Group ON WatchItems (GuildId, GroupId);
        CREATE UNIQUE INDEX UX_WatchItems_Media ON WatchItems (GroupId, Source, ExternalId) WHERE Source IS NOT NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE WatchItems; DROP TABLE MediaCache;
        ALTER TABLE PollOptions DROP COLUMN ExternalId; ALTER TABLE PollOptions DROP COLUMN Source;
        ALTER TABLE Polls DROP COLUMN Finalized; ALTER TABLE Polls DROP COLUMN MessageId; ALTER TABLE Polls DROP COLUMN ChannelId;
        """);
}
