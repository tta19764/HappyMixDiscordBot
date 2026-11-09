# HappyMix implementation roadmap

Reviewed: 2026-11-09. This is a source-code audit, not confirmation of a live deployment.

Rechecked against the working tree: the current implementation now supports exact main-team CS2 and Dota 2 filtering, but still uses in-memory announcement state. SQLite remains the selected next persistence task. Day 24 is implemented early; Day 23 is partial because per-guild subscription controls are not yet implemented. See ESPORTS_IMPLEMENTATION.md for this increment's plan and verification.

## How to maintain this plan

Edit the tables below, then run `python tools/build_progress.py` from the repository root.
Open `docs/progress.html` directly in a browser. No server, npm install, or paid hosting is needed.
Commit the Markdown and generated HTML together. The dashboard is read-only; it never silently saves changes in browser storage.

Statuses: `done`, `partial`, `planned`, `blocked`. Done means the acceptance criterion is implemented and verified; partial means some implementation exists. Record evidence when changing status.
Days are adjustable work slots, not dates or promises. This is a fresh 30-slot plan from the current baseline, not a claim that 30 days have elapsed.
Progress is counted by completed items, not estimated hours. Baseline capabilities and future plan are counted separately.

## Current baseline

| ID | Area | Status | Capability | Evidence / limitation |
| --- | --- | --- | --- | --- |
| B01 | Foundation | done | .NET 10 layered application with Discord.Net and MediatR | src/HappyMix.Bot/Program.cs; src/HappyMix.Application/DependencyInjection.cs |
| B02 | Foundation | done | Discord connection and /ping command | src/HappyMix.Bot/Services/DiscordBotWorker.cs; src/HappyMix.Bot/Interactions/GeneralModule.cs |
| B03 | Esports | done | Paginated CS2 and Dota 2 fixtures for exact main-team IDs | PandaScoreClient.cs; NAVI/FUT/Monte CS2, NAVI/Monte Dota; no FUT Dota identity found; local-day and overnight handling tested |
| B04 | Esports | done | Main-team filtering at all tiers, optional tier restriction and game/tournament pages | PandaScoreOptions.cs; DiscordMatchAnnouncementPublisher.cs; no junior subscriptions; old general S/A feed replaced |
| B05 | Esports | done | Starting-soon, map/game and series-result announcements | SynchronizeMatchesCommandHandler.cs; completed games handled while running or finished; game-scoped in-memory deduplication |
| B06 | Foundation | done | Minute-based Quartz job without overlapping executions | src/HappyMix.Bot/Jobs/SynchronizeMatchesJob.cs; Configuration/DependencyInjection.cs |
| B07 | Operations | partial | Docker image and local development compose | Dockerfile; compose.development.yaml; no production deployment evidence |
| B08 | Reliability | partial | Announcement deduplication | src/HappyMix.Infrastructure/Notifications/InMemoryMatchAnnouncementState.cs; lost after restart |
| B09 | Quality | done | Application, provider, scheduling and embed tests | 57 tests passed using installed .NET SDK 10.0.402 from an isolated validation working directory; repository SDK pin unchanged |

## Next 30 work slots

| Day | Area | Status | Deliverable | Acceptance criterion | Depends on |
| --- | --- | --- | --- | --- | --- |
| 1 | Reliability | planned | SQLite foundation and persistent announcement history | Add EF Core SQLite and initial migration; replace B08 in-memory state; configure persistent database path; restart after successful publication produces no repeated announcements; migration and restart test pass; document the send-before-save crash window | B02 |
| 2 | Operations | planned | Production hosting and deployment | Check current checkout total is at most $5/month including tax; deploy one instance with persistent disk, secrets and auto-restart; confirm uptime remotely | 1 |
| 3 | Foundation | planned | Guild settings and access policy | Store channel/timezone settings per guild; only configured administrators can change them | 1 |
| 4 | Groups | planned | Saved groups and organizers | Create/invite/join/leave a group; only organizer or moderator edits membership; isolate guilds | 3 |
| 5 | Availability | planned | One-off availability windows | Add/edit/remove available windows; reject invalid or overlapping input; support windows across midnight | 3 |
| 6 | Availability | planned | Daily board and confirmations | One message per local date in #availability; buttons confirm/change/unavailable; only explicit confirmation means confirmed | 5 |
| 7 | Availability | planned | Weekly recurring availability | Generate expected windows from weekly patterns in member timezone; handle daylight-saving boundaries | 5 |
| 8 | Availability | planned | Exceptions and opt-in prompts | Date exception overrides recurrence; snooze and quiet hours prevent unwanted prompts; keep commitment reasons private | 6, 7 |
| 9 | Availability | planned | Weekly overview and overlap finder | Show confirmed versus expected hours separately; find shared windows of a requested duration | 8 |
| 10 | Steam | planned | Profile linking and library import | Import a real public library by Steam ID/profile URL; resolve vanity names; show privacy errors; label link self-declared | 3 |
| 11 | Steam | planned | Willingness preferences | Search/paginate imported games; save want/open/not-interested per member | 10 |
| 12 | Steam | planned | Refresh and disconnect | Daily/manual sync preserves preferences; failed sync never clears library; disconnect removes imported association/data | 11 |
| 13 | Steam | planned | Common game recommendations | Rank shared owned games using willingness; permit catalog additions for missing free games; show why each is suggested | 4, 9, 12 |
| 14 | Sessions | planned | Session creation and attendance | Create/edit/cancel session for server, role or group; enforce eligibility and capacity on every interaction | 4, 13 |
| 15 | Sessions | planned | Waitlist and readiness | Concurrent joins cannot exceed capacity; timed slot offer promotes next eligible member; readiness buttons work | 14 |
| 16 | Sessions | planned | Durable session reminders | Reminders respect timezones/preferences and survive restart; cancellation suppresses future sends | 15 |
| 17 | Voting | planned | Game-buying poll creation | Choose candidates, deadline, minimum buyers and eligible server/role/group/user audience; snapshot electorate at creation | 4, 10 |
| 18 | Voting | planned | Votes and result calculation | Separate would-buy/already-own/pass; allow changes before deadline; enforce eligible one vote per candidate per user; explain ties | 17 |
| 19 | Voting | planned | Vote-to-group workflow | Close poll durably, retain summary, create group and propose first session; store links rather than assuming universal prices | 14, 18 |
| 20 | Groups | planned | Temporary roles and text spaces | Optional bot-owned role/channel grants only intended access; creation limits and organizer controls work; recover partial creation failures | 4, 19 |
| 21 | Groups | planned | Temporary voice spaces | Create room; organizer can rename/lock/set capacity through bot; permissions remain scoped to their room | 20 |
| 22 | Reliability | planned | Resource lifecycle and cleanup | Persist IDs/expiry; extend/close controls; lock text before later deletion; never remove occupied voice room or unrelated resource; reconcile after restart | 21 |
| 23 | Esports | partial | Team subscriptions and provider validation | Verified exact main-roster IDs and live API access; defaults exclude juniors; no FUT Dota identity found. Remaining: per-guild subscription controls | 3 |
| 24 | Esports | done | Dota 2 adapter and shared match model | Shared CS2/Dota models, paginated feeds, labelled schedules/results, game-scoped deduplication; fixture tests pass; six live request shapes validated | 23 |
| 25 | Esports | planned | Schedule changes and spoiler controls | Persist message references; update reschedules/cancellations; track cross-midnight matches; suppress or hide result spoilers per channel setting | 1, 24 |
| 26 | Esports | planned | Watch parties | Match notification creates group/session with attendance; participant reminders; optional temporary room | 16, 22, 25 |
| 27 | Notifications | planned | Digest and targeted invitations | Weekly digest combines sessions/esports/availability; game-interest opt-in invitations; snooze, quiet hours and mention limits | 9, 16, 26 |
| 28 | Quality | planned | End-to-end pilot and recovery checks | Friends exercise availability -> suggestion -> vote -> group -> session; verify unauthorized interactions and retries after failures | 27 |
| 29 | Operations | planned | Backup, restore and resource budget | Restore SQLite to a fresh instance; measure idle/peak memory on chosen host; inspect API allowance and monthly spend; document rollback | 28 |
| 30 | Release | planned | Fixes, walkthrough and next backlog | Resolve pilot blockers; document commands and permissions; review acceptance criteria; explicitly carry incomplete scope forward | 29 |

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
- Recurring sessions and advanced availability poll voting.
- Match prediction leaderboard without money or betting.
- Regional price alerts from a verified permitted data source.
- Calendar export and optional external calendar synchronization.

## SQLite implementation decision

- Use SQLite through EF Core in Infrastructure, keeping Application abstractions independent of the storage provider. Add a context and versioned migrations; do not replace migrations with EnsureCreated for an evolving database.
- First persist announcement records with unique event keys. Later migrations add guild settings, groups, availability, Steam libraries/preferences, votes, sessions, reminders and bot-created resource IDs.
- Keep one bot instance and one local database file on persistent storage. In Docker, mount a writable data directory owned by the runtime user; do not store the database only in the container's disposable layer.
- Use short database operations and scoped contexts; do not share a context across concurrent Discord interactions and Quartz jobs. Add uniqueness constraints for duplicate votes, attendance and announcement keys.
- Keep Quartz scheduling initially; persist application reminder state in SQLite so restarts can recover overdue work. A persistent Quartz job store is a separate decision, not an implicit part of adding SQLite.
- SQLite cannot make a Discord message send and a database commit atomic. Restart deduplication covers successfully recorded publications; explicitly test and document delivery recovery for a crash between send and save. Do not promise exactly-once external delivery.
- Back up using a SQLite-aware consistent snapshot and test restoration. Do not assume copying a live database file alone is sufficient, particularly when WAL is enabled.
- No existing on-disk database needs conversion: current announcement history is process memory only. Plan the first rollout to avoid replaying all of today's notifications, with an explicit initial-history policy.

## Audit cautions

- Exact NAVI/FUT/Monte main-team allowlists now replace the general CS2 tier feed. Per-guild subscription editing is still outstanding; no FUT Dota main identity was returned by PandaScore.
- Current daily schedule publishes once per process/local date, not at a configured morning time, and does not edit schedule changes.
- Current announcement state is global/in-memory and has no persistent guild-specific notification preferences.
- Existing local changes were present during this review; this roadmap does not certify them as released.
- The repository still pins SDK 10.0.102 with latestPatch. Tests were successfully executed with installed SDK 10.0.402 from work/sdk-validation using a local SDK selection file; the root pin is unchanged. Normal root-directory dotnet commands still require the pinned SDK or an explicitly reviewed SDK-policy update.
