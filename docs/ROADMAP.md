# HappyMix implementation roadmap

Reviewed: 2026-11-15.

Days 1–6 record delivered milestones. Day 7 includes all changes starting with linked accounts, through the current dashboard, command UX, Twitch, light-theme and event work. Day 7 remains partial while the light-theme redesign and events are in progress. The previous 142-test result covers the linked-account milestone; it does not certify the current working tree.

## How to maintain this plan

Edit the tables below, then run `python tools/build_progress.py` from the repository root.
Open `docs/progress.html` directly in a browser. No server, npm install, or paid hosting is needed.
Commit the Markdown and generated HTML together. The dashboard is read-only; it never silently saves changes in browser storage.

Statuses: `done`, `partial`, `planned`, `blocked`. Done means the acceptance criterion is implemented and verified; partial means some implementation exists. Record evidence when changing status.
Days are adjustable work slots, not dates or promises. The first six slots summarize delivered work; Day 7 groups development starting with linked accounts.
Progress is counted by completed items, not estimated hours. Baseline capabilities and development days are counted separately.

## Current baseline

| ID | Area | Status | Capability | Evidence / limitation |
| --- | --- | --- | --- | --- |
| B01 | Foundation | done | .NET 10 layered application with Discord.Net and MediatR | src/HappyMix.Bot/Program.cs; src/HappyMix.Application/DependencyInjection.cs |
| B02 | Foundation | done | Discord connection and /ping command | src/HappyMix.Bot/Services/DiscordBotWorker.cs; src/HappyMix.Bot/Interactions/GeneralModule.cs |
| B03 | Esports | done | Paginated CS2 and Dota 2 fixtures for exact main-team IDs | PandaScoreClient.cs; NAVI/FUT/Monte/B8/Inner Circle/G2 CS2, NAVI/Monte Dota; no FUT Dota identity found; local-day and overnight handling tested |
| B04 | Esports | done | Main-team filtering at all tiers, optional tier restriction and game/tournament pages | PandaScoreOptions.cs; DiscordMatchAnnouncementPublisher.cs; no junior subscriptions; old general S/A feed replaced |
| B05 | Esports | done | Starting-soon, map/game and series-result announcements | SynchronizeMatchesCommandHandler.cs; completed games handled while running or finished; channel/game-scoped SQLite history |
| B06 | Foundation | done | Minute-based Quartz job without overlapping executions | src/HappyMix.Bot/Jobs/SynchronizeMatchesJob.cs; Configuration/DependencyInjection.cs |
| B07 | Operations | done | Docker image and persistent production volume | Oracle container running commit b4e61af with happymix-data mounted at /app/data |
| B08 | Reliability | done | Persistent announcement deduplication | Infrastructure/Persistence; EF migration, per-channel/game/event keys and first-run seeding; real-file restart tests pass; send-before-save crash window documented |
| B09 | Quality | done | Application, provider, scheduling, embed and SQLite integration tests | 87 tests passed using installed .NET SDK 10.0.402 and isolated artifacts |

## Development checklist

| Day | Area | Status | Deliverable | Acceptance criterion | Depends on |
| --- | --- | --- | --- | --- | --- |
| 1 | Reliability | done | SQLite and MediatR foundation | Startup migrations, persistent announcement history, application command and query handlers, and architecture tests. | B02 |
| 2 | Operations | done | Oracle deployment and HTTPS | ARM64 images deploy through GitHub Actions and GHCR over Tailscale. Caddy served the dashboard through Duck DNS with a valid certificate. Recovery and spending checks remain on Day 30. | 1 |
| 3 | Settings | done | Server settings and esports delivery | Discord and web settings share persisted preferences. CS2 and Dota 2 have separate routing, team catalogs, reminders, and Twitch preferences. Web announcement selectors accept standard text channels. | 1 |
| 4 | Groups | done | Groups, membership, and game attachments | Invite-only groups, descriptions, rosters, role repair, and the web workspace. Games can be attached with an optional Steam App ID. | 3 |
| 5 | Availability | done | Personal and recurring availability | Today and weekly timetable entry, named time zones, fixed offsets, overnight windows, and full-day shortcuts. MVC dashboard and English, Ukrainian, and Russian resources. | 2, 3 |
| 6 | Availability | done | Group member availability board | Seven day tabs, member rows with No data, and distinct one-off and recurring hours with overnight carryover. Authorization and schedule behavior covered by tests. | 4, 5 |
| 7 | Identity, dashboard and events | partial | Linked accounts and subsequent development | Linking needs the primary's short-lived single-use request and the alternate's own Discord sign-in and approval; both must be server members. Each family is one member for groups, organizer rights, availability and board counts, in the dashboard and slash commands. Discord admin and moderator rights stay per account. Invitations and group roles apply only to the addressed account. The roster and board show that account first. Link and unlink previews, explicit conflict resolution and restore on unlink. AccountLinkTests: 14 tests; 142 total passed at that milestone. Subsequent work includes multiple availability ranges and schedule preservation on linking, admin linked-account list, weekly sliders and 24-hour inputs, searchable settings editors, light theme and navigation, named-time-zone command suggestions, live Twitch checks for reminders/start/map results, README/shared info/help updates, and natural Ukrainian/Russian wording for settings, availability and events, and migration to 623 stable keys shared by explicit English/Ukrainian/Russian resources with English fallback. Four localization tests pass; resource parity and placeholders validated. Light-theme redesign and group/standalone recurring events are currently in progress. Latest build passed using SDK 10.0.402 with duplicate translation-resource warnings. Full current-suite and live verification pending. | 4, 6 |
| 8 | Steam and Discord | partial | Game news subscriptions, dashboard link, event commands, v0.0.2 notification controls, and v0.0.4 community defaults and channel updates | Members can create and join Steam news channels and roles from the dashboard and slash commands. Creators and admins can delete subscriptions. Verify Discord resource creation, role pings, and cleanup on a live server. Verify prior dashboard, account-linking, and event flows on the deployed build. | 7 |
| 9 | Availability | planned | Exceptions and opt-in prompts | Date exception overrides recurrence; snooze and quiet hours prevent unwanted prompts; keep commitment reasons private | 6, 8 |
| 10 | Availability | planned | Weekly overview and overlap finder | Show confirmed versus expected hours separately; find shared windows of a requested duration | 9 |
| 11 | Steam | planned | Profile linking and library import | Import a real public library by Steam ID/profile URL; resolve vanity names; show privacy errors; label link self-declared | 3 |
| 12 | Steam | planned | Willingness preferences | Search/paginate imported games; save want/open/not-interested per member | 11 |
| 13 | Steam | planned | Refresh and disconnect | Daily/manual sync preserves preferences; failed sync never clears library; disconnect removes imported association/data | 12 |
| 14 | Steam | planned | Common game recommendations | Rank shared owned games using willingness; permit catalog additions for missing free games; show why each is suggested | 4, 10, 13 |
| 15 | Sessions | planned | Session creation and attendance | Create/edit/cancel session for server, role or group; enforce eligibility and capacity on every interaction | 4, 14 |
| 16 | Sessions | planned | Waitlist and readiness | Concurrent joins cannot exceed capacity; timed slot offer promotes next eligible member; readiness buttons work | 15 |
| 17 | Sessions | planned | Durable session reminders | Reminders respect timezones/preferences and survive restart; cancellation suppresses future sends | 16 |
| 18 | Voting | planned | Game-buying poll creation | Choose candidates, deadline, minimum buyers and eligible server/role/group/user audience; snapshot electorate at creation | 4, 11 |
| 19 | Voting | planned | Votes and result calculation | Separate would-buy/already-own/pass; allow changes before deadline; enforce eligible one vote per candidate per user; explain ties | 18 |
| 20 | Voting | planned | Vote-to-group workflow | Close poll durably, retain summary, create group and propose first session; store links rather than assuming universal prices | 15, 19 |
| 21 | Groups | planned | Temporary roles and text spaces | Optional bot-owned role/channel grants only intended access; creation limits and organizer controls work; recover partial creation failures | 4, 20 |
| 22 | Groups | planned | Temporary voice spaces | Create room; organizer can rename/lock/set capacity through bot; permissions remain scoped to their room | 21 |
| 23 | Reliability | planned | Resource lifecycle and cleanup | Persist IDs/expiry; extend/close controls; lock text before later deletion; never remove occupied voice room or unrelated resource; reconcile after restart | 22 |
| 24 | Esports | planned | Live provider and routing regression pass | Verify saved per-game channels and team filters against live CS2 and Dota 2 notifications. | 3, 8 |
| 25 | Esports | planned | Announcement recovery verification | Verify deduplication across restart and recovery from provider errors in the live workflow. | 24 |
| 26 | Esports | planned | Schedule changes and spoiler controls | Persist message references; update reschedules/cancellations; track cross-midnight matches; suppress or hide result spoilers per channel setting | 1, 25 |
| 27 | Esports | planned | Watch parties | Match notification creates group/session with attendance; participant reminders; optional temporary room | 17, 23, 26 |
| 28 | Notifications | planned | Digest and targeted invitations | Weekly digest combines sessions/esports/availability; game-interest opt-in invitations; snooze, quiet hours and mention limits | 10, 17, 27 |
| 29 | Quality | planned | End-to-end pilot and recovery checks | Friends exercise availability -> suggestion -> vote -> group -> session; verify unauthorized interactions and retries after failures | 28 |
| 30 | Operations | planned | Backup, restore and resource budget | Restore SQLite to a fresh instance; measure idle/peak memory on chosen host; inspect API allowance and monthly spend; document rollback | 29 |
| 31 | Release | planned | Fixes, walkthrough and next backlog | Resolve pilot blockers; document commands and permissions; review acceptance criteria; explicitly carry incomplete scope forward | 30 |

## Scope and operating decisions

- One .NET process, existing Clean Architecture, SQLite and existing Quartz scheduler. No React runtime, Redis or managed database required.
- Host candidate: $4 DigitalOcean VM from the discussion; price, tax, availability and memory suitability must be rechecked on Day 2. A deployment is not currently verified. Do not assume a $5 minimum usage plan is a fixed $5 bill.
- The bot should run continuously; do not use a sleeping web tier for precise reminders. No domain is required for the initial gateway-based Discord bot.
- Steam import uses the official API and visible libraries. It does not bypass profile privacy or prove ownership of the linked account.
- Daily availability means one refreshed board per date in a configured channel, with optional daily discussion thread. Dedicated channels for every date are not the default.
- Roles are optional for polls; snapshot eligible users in storage. Check authorization again for every button interaction. Temporary resources stay under configured categories and below the bot role.
- Free esports provider coverage is an acceptance gate, not an assumption. Avoid introducing paid live feeds or scraping as an unnoticed dependency.
- Each slot should fit a short human review, but agent implementation can take longer. If work slips, move it forward; reduce stretch scope before skipping reliability or access checks.

## Stretch backlog

- Join-to-create voice lobby with ownership transfer.
- Verified Steam sign-in (requires an additional web authentication flow).
- Advanced availability poll voting. Recurring events are included in Day 7.
- Match prediction leaderboard without money or betting.
- Regional price alerts from a verified permitted data source.
- Calendar export and optional external calendar synchronization.

## SQLite implementation decision

- Use SQLite through EF Core in Infrastructure, keeping Application abstractions independent of the storage provider. Add a context and versioned migrations; do not replace migrations with EnsureCreated for an evolving database.
- Announcement records with unique event keys are implemented. Later migrations add guild settings, groups, availability, Steam libraries/preferences, votes, sessions, reminders and bot-created resource IDs.
- Keep one bot instance and one local database file on persistent storage. In Docker, mount a writable data directory owned by the runtime user; do not store the database only in the container's disposable layer.
- Use short database operations and scoped contexts; do not share a context across concurrent Discord interactions and Quartz jobs. Add uniqueness constraints for duplicate votes, attendance and announcement keys.
- Keep Quartz scheduling initially; persist application reminder state in SQLite so restarts can recover overdue work. A persistent Quartz job store is a separate decision, not an implicit part of adding SQLite.
- SQLite cannot make a Discord message send and a database commit atomic. Restart deduplication covers successfully recorded publications; explicitly test and document delivery recovery for a crash between send and save. Do not promise exactly-once external delivery.
- Back up using a SQLite-aware consistent snapshot and test restoration. Do not assume copying a live database file alone is sufficient, particularly when WAL is enabled.
- The first rollout from the old bot has no on-disk history to import. Completed results are seeded once by default; schedule/upcoming alerts can still post once. All later starts reuse SQLite history. See SQLITE_DAY1.md.

## Audit cautions

- Exact main-team allowlists replace the general CS2 tier feed. Administrators can change their server's list with `/settings teams`. PandaScore returned no FUT Dota main roster.
- The daily schedule publishes once per persisted destination/local date, not at a configured morning time, and does not edit schedule changes.
- Announcement state is now persisted per destination channel and discipline. Guild-specific notification preferences are implemented; live routing verification remains on Day 24.
- Existing local changes were present during this review; this roadmap does not certify them as released.
- The repository still pins SDK 10.0.102 with latestPatch. Tests were successfully executed with installed SDK 10.0.402 from work/sdk-validation using a local SDK selection file; the root pin is unchanged. Normal root-directory dotnet commands still require the pinned SDK or an explicitly reviewed SDK-policy update.
