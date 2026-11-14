# Groups workspace: developer handoff

The dashboard gains a **Groups** tab next to Availability (and Settings for admins). It is a web client for the
same group use cases the `/group` slash commands use, so web and Discord changes write the same saved records.

Status: implemented, builds, 119 tests pass (23 new). Rendered with mock data at desktop and phone widths in
English, Ukrainian and Russian. **Not yet exercised against a live Discord server** (see the last section).

## Routes

All routes require the Discord OAuth session. Every request re-reads the acting user's membership of the server
through Discord REST; a group or invite URL alone never grants access.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/groups/{guildId}?filter=mine\|invited\|all&q=&page=` | Directory. Filter, search and page live in the query string, so Back restores them. |
| GET | `/groups/{guildId}/new` | Create form (name, optional description, up to 3 games). |
| POST | `/groups/{guildId}/new` | Create. Redirects to the new group. |
| GET | `/groups/{guildId}/{groupId}?back=` | Group detail: Overview, Members, Games. `back` must point inside `/groups/{guildId}`. |
| POST | `/groups/{guildId}/{groupId}/invitation` | `decision=accept\|decline` for the signed-in user's own invitation. |
| POST | `/groups/{guildId}/{groupId}/leave` | Leave (members only; organizers must close). |
| POST | `/groups/{guildId}/{groupId}/description` | Edit description. |
| POST | `/groups/{guildId}/{groupId}/invite` | `member=<username or user ID>`; verified as a human member of the server, then DMed Accept/Decline buttons. |
| POST | `/groups/{guildId}/{groupId}/members/{memberId}/remove` | Remove a member or (`?kind=invite`) revoke an invitation. |
| POST | `/groups/{guildId}/{groupId}/members/{memberId}/repair` | Moderators: repair one member's group roles. |
| POST | `/groups/{guildId}/{groupId}/games` | Add a game (`name`, optional `steam`). |
| POST | `/groups/{guildId}/{groupId}/games/{gameId}` | Rename a game or link/unlink Steam. |
| POST | `/groups/{guildId}/{groupId}/games/{gameId}/remove` | Remove a game. |
| POST | `/groups/{guildId}/{groupId}/close` | Close the group through the existing `DeleteGroupCommand` (deletes the Discord role). |
| POST | `/groups/{guildId}/repair-roles` | Moderators: repair group roles for the whole server. |

All POSTs use the existing antiforgery token and redirect after success. Failures re-render the page with the
error beside the affected section and the user's input kept.

## Permission rules

Enforced in the Application handlers (`GroupWorkspaceQueries`, `GroupCommands`); the UI only hides what the
handler would refuse. Moderator = Discord **Manage Server** or **Administrator**, the same rule as the slash commands.

| Who | Directory | Detail | Actions |
| --- | --- | --- | --- |
| Not related to the group | Not listed | "Not available" (same answer as a missing group) | None |
| Invited | Listed under Pending invitations | Overview only: name, description, organizer, member count | Accept, Decline |
| Member | Listed under My groups | Overview, full roster, games | Leave |
| Organizer | Listed under My groups | Everything, including pending invitations | Edit description, invite, remove, revoke, games, close |
| Moderator | Every group in the server | Everything | Same as organizer, plus single-member and server-wide role repair |

- Groups stay invite-only. No member-wide discovery was added.
- Creation follows the existing organizer-role setting (`CanCreateGroupQuery` mirrors `CreateGroupCommand`).
- Every query and command is scoped to the server; a group ID from another server never resolves.
- Commands that take a group ID re-check visibility first, so a modified form targeting a hidden group gets
  the same "doesn't exist or you don't have access" answer.
- Revoked or already-resolved invitations can't be accepted, by name or by DM token.
- If a change saves but Discord role sync fails, the page says so and points moderators to
  **Groups → Discord role repair** or `/group repair-roles`.

## Data and migration

Migration `20261115000100_AddGroupIdsAndGames`:

- `GuildGroups.GroupId` (TEXT, unique index): a durable GUID per group. Existing rows are backfilled with
  random version-4 GUIDs in SQL. The JSON document (name, members, invitations, tokens, description, role ID) is
  not touched. The store also assigns an ID to any row that is still missing one on first read.
- New table `GuildGroupGames`: `Id`, `GuildId`, `GroupId`, `Name`, `Provider`, `ExternalId`, `AddedAtUtc`.
  Unique index on `(GroupId, Provider, ExternalId)` rejects a duplicate Steam App ID within a group; games
  without Steam (null provider) never collide. Closing a group deletes its games in the same transaction.
- Covered by `UpgradingAnExistingDatabaseBackfillsDurableIdsWithoutChangingGroups`, which builds a database at the
  previous migration with legacy rows and upgrades it.

Back up the SQLite database (`Storage:DatabasePath`) before deploying; `Down` drops the new table and column.

## Steam extension points

- `SteamApps.TryNormalize` accepts `730`, `0730`, `store.steampowered.com/app/730/...` and
  `steamcommunity.com/app/730`, and stores the canonical number. `SteamApps.StoreUrl` builds the
  View on Steam link from the validated ID only.
- Games store `Provider = "steam"` and `ExternalId = "<app id>"`, so later ownership checks or library imports can
  join on `(Provider, ExternalId)`. A game can be added without Steam and linked later (`UpdateGroupGameCommand`).
- No Steam API key is needed. Steam account linking, library import and ownership checks are left for a later
  integration; add a per-user `(Provider, ExternalAccountId)` link table and join it to `GuildGroupGames.ExternalId`.

## Discord adapters

- `IMemberDirectory` (`DiscordMemberDirectory`) resolves display names and avatars for a whole server with paged
  REST calls (1,000 members per page), cached 60 seconds per server: no request per roster member. Members who left
  show as "Former member · …1234". Names are always plain text, never mentions.
- `GroupInviteMessages` sends the Accept/Decline DM and is now shared by `/group invite` and the dashboard.

## Code map

- Application: `Guilds/GroupWorkspace.cs` (queries, records, Steam parsing), `Guilds/GroupGameCommands.cs`,
  additions in `GroupCommands.cs` and `GroupRequests.cs` (`CreateGroupCommand.Games`, `GroupChange.GroupId`).
- Infrastructure: `SqliteGuildGroupStore.cs`, `HappyMixDbContext.cs`, the new migration and snapshot.
- Web: `Web/DashboardGroups.cs`, `Web/GroupViewModels.cs`, `Views/Dashboard/Groups|Group|NewGroup.cshtml`,
  partials `_Confirm`, `_Avatar`, `_GroupBadge`; Groups tab in `_ServerNav`; CSS section 18; confirm-panel JS.
- Resources: about 150 new strings in `DashboardResources.uk.resx` and `.ru.resx`.
- Tests: `tests/HappyMix.Application.Tests/Groups/GroupWorkspaceTests.cs`.

## Needs live Discord verification

1. **Server Members Intent.** Listing members needs it enabled in the Developer Portal. Without it, rosters
   fall back to "Member · …1234" and the invite picker has no suggestions (typing a user ID still works).
   Server-wide role repair already depends on the same intent.
2. Display names, server avatars and the "left the server" fallback against real members.
3. Invites from the web: the DM arrives with working Accept/Decline buttons, and accepting in Discord updates the page.
4. Closing a group from the web deletes its Discord role; role sync failure (for example, a role above the bot)
   shows the warning banner.
5. Moderator detection (Manage Server / Administrator) for a non-admin moderator.
6. Migration on a copy of the production database.

## Known gaps and follow-ups

- On phones, the header's language buttons (added with the localization work) stack and overflow the header.
- Group names can't be renamed (unchanged); names are still the key for the existing commands, with the durable
  ID used for the web routes and games.
- The create form takes up to three games; more can be added on the group page.
