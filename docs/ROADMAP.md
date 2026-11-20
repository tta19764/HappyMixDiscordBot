# HappyMix implementation roadmap

Reviewed: 2026-11-20.

Reviewed repository tags and the English, Ukrainian and Russian release manifests through `v0.0.12` (`78409c9`). GitHub has no separate Release entries; tags and `src/HappyMix.Bot/Releases/v*.json` provide the release history. Basic voting has already been presented; watchlists, series progress, logs and the dashboard redesign have not. The two-day catch-up below covers implemented features without scheduling them for development again. Tagged source does not establish the currently deployed version or completion of a live walkthrough.

## Presentation catch-up

Dates use Europe/Kiev. Both presentations are planned; mark each done only after presenting it. Allow about 15–20 minutes each. Use the current behavior described below: later releases supersede some earlier voting controls. Days in the development checklist remain adjustable implementation slots, independent of these dates.

| Date | Focus | Status | Features | Walkthrough / completion criterion | Release sources |
| --- | --- | --- | --- | --- | --- |
| 2026-11-20 (today) | Dashboard, watchlists and votes | planned | New dashboard navigation and Night/Sand themes; group tabs; series/film/anime watchlists and progress; current voting layout | Tour the side panel, themes and group tabs (3 min). In a group tagged Series, Films or Anime, add a catalog title or a vote winner, set Planned/Watching/Watched/Dropped, mark an episode watched, show season rollover and save a resume timestamp; show valid season/episode limits and Finish series (8 min). Briefly revisit familiar voting through Active / Start a vote / Results, title cards, voters and day/week results; show the native Discord poll and chosen-member private thread (5 min). Do not repeat the basic voting introduction. | v0.0.7 watchlists; v0.0.8 search correction; v0.0.9 watchlist validation and current native polls; v0.0.11 redesign; v0.0.12 vote tabs |
| 2026-11-21 (tomorrow) | Community, events, news and logs | planned | Open groups and join requests; member game suggestions and multi-platform search; game news sources; whole-series confirmation; admin Logs | Request membership in an open group and approve it as organizer; suggest and accept a non-Steam game (5 min). Show Patch notes / Announcements / Press articles and explain that newly enabled sources start from now (2 min). Confirm all pending upcoming dates of a recurring event and show the single confirmation message per accepted person (3 min). As server admin, filter Logs by action/user text, expand details, enable Live and export CSV; explain access, masking and 14-day default retention (5 min). | v0.0.9 open groups, game search and logs; v0.0.10 news sources and quieter release logging; v0.0.12 event confirmation |

Preparation: use a group with the appropriate media tag, a catalog-backed series with season data, a vote winner, a recurring event with pending dates and accepted participants, an open group, a news subscription and a server administrator account. Prepare an ordinary member account for join requests and permission checks. Demonstrate Finish series on a disposable watchlist entry. Check Discord posting permissions, private-thread permissions and configured catalog credentials before the session; film search requires TMDB configuration and multi-platform game search requires Twitch/IGDB credentials. Check the deployed build supports these features before presenting. Keep any unrelated local UI edits out of the release claims.

If time is tight, keep the watchlist progress demo today and the Logs demo tomorrow; shorten the navigation tour, voting recap and game-search examples. Carry unfinished presentation items forward explicitly. Earlier notification and invitation changes in v0.0.2–v0.0.5 stay available as recap material, rather than assuming they are newly presented.

## How to maintain this plan

Edit the tables below, then run `python tools/build_progress.py` from the repository root.
Open `docs/progress.html` directly in a browser. No server, npm install, or paid hosting is needed.
Commit the Markdown and generated HTML together. The dashboard is read-only; it never silently saves changes in browser storage.

Statuses: `done`, `partial`, `planned`, `blocked`. Done means the acceptance criterion is implemented and verified; partial means some implementation exists. Record evidence when changing status.
Days are adjustable work slots, not dates or promises. The first seven slots summarize delivered work, with later rows separating remaining scope from released foundations. Presentation dates above do not change implementation status.
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
| B07 | Operations | done | Docker image and persistent production volume | Historical deployment verified at b4e61af with happymix-data mounted at /app/data; current deployed tag not checked in this review |
| B08 | Reliability | done | Persistent announcement deduplication | Infrastructure/Persistence; EF migration, per-channel/game/event keys and first-run seeding; real-file restart tests pass; send-before-save crash window documented |
| B09 | Quality | done | Application, provider, scheduling, embed and SQLite integration tests | Historical baseline: 87 tests passed using installed .NET SDK 10.0.402 and isolated artifacts; this plan update validates roadmap generation and release manifests, not the current full .NET suite |
| B10 | Media | done | Group watchlists and series progress | v0.0.7 and v0.0.9: catalog titles, statuses, next episode, season rollover, resume position, validated seasons/episodes and Finish series; README feature guide |
| B11 | Voting | done | General group/server voting and Discord polls | v0.0.6–v0.0.9 and v0.0.12: tags, catalog suggestions/cards, native Discord polls, private threads for selected people, voter/results views and three dashboard tabs; buying-specific decisions remain below |
| B12 | Administration | done | Admin Logs, open groups and game suggestions | v0.0.9: server-scoped admin access, filters, Live, CSV and retention; join requests, game suggestions and multi-platform catalog support; live workflow verification remains |
| B13 | Dashboard | done | Redesigned dashboard | v0.0.11–v0.0.12: side panel, Night/Sand themes, group tabs, icons, localized labels and unclipped menus; unrelated local edits are not included |
| B14 | Notifications | done | News source controls and recurring-event improvements | v0.0.2–v0.0.5, v0.0.10 and v0.0.12: notification routes, whole-series responses, cleanup, selectable Steam feeds and whole-series confirmation; live delivery checks remain |

## Development checklist

| Day | Area | Status | Deliverable | Acceptance criterion | Depends on |
| --- | --- | --- | --- | --- | --- |
| 1 | Reliability | done | SQLite and MediatR foundation | Startup migrations, persistent announcement history, application command and query handlers, and architecture tests. | B02 |
| 2 | Operations | done | Oracle deployment and HTTPS | ARM64 images deploy through GitHub Actions and GHCR over Tailscale. Caddy served the dashboard through Duck DNS with a valid certificate. Recovery and spending checks remain on Day 30. | 1 |
| 3 | Settings | done | Server settings and esports delivery | Discord and web settings share persisted preferences. CS2 and Dota 2 have separate routing, team catalogs, reminders, and Twitch preferences. Web announcement selectors accept standard text channels. | 1 |
| 4 | Groups | done | Groups, membership, and game attachments | Invite-only groups, descriptions, rosters, role repair, and the web workspace. Games can be attached with an optional Steam App ID. | 3 |
| 5 | Availability | done | Personal and recurring availability | Today and weekly timetable entry, named time zones, fixed offsets, overnight windows, and full-day shortcuts. MVC dashboard and English, Ukrainian, and Russian resources. | 2, 3 |
| 6 | Availability | done | Group member availability board | Seven day tabs, member rows with No data, and distinct one-off and recurring hours with overnight carryover. Authorization and schedule behavior covered by tests. | 4, 5 |
| 7 | Identity, dashboard and events | done | Linked accounts, localized dashboard and recurring events | Included in v0.0.1–v0.0.5, with the completed redesign in v0.0.11–v0.0.12. Linked accounts share membership and availability while Discord permissions stay per account. Recurring events support series answers, per-date withdrawals, notifications and whole-series confirmation. en/uk/ru release manifests and README document the delivered behavior; historical test counts are not current validation. | 4, 6 |
| 8 | Steam and Discord | partial | Released news and notification features; live walkthrough outstanding | v0.0.1–v0.0.5 and v0.0.10 include subscription channels/roles, dashboard and command controls, notification defaults, channel updates and selectable news sources. Verify resource creation, role pings, cleanup, linking and events on the deployed build. Presentation coverage is scheduled separately above. | 7 |
| 9 | Availability | planned | Exceptions and opt-in prompts | Date exception overrides recurrence; snooze and quiet hours prevent unwanted prompts; keep commitment reasons private | 6, 8 |
| 10 | Availability | planned | Weekly overview and overlap finder | Show confirmed versus expected hours separately; find shared windows of a requested duration | 9 |
| 11 | Steam | planned | Profile linking and library import | Import a real public library by Steam ID/profile URL; resolve vanity names; show privacy errors; label link self-declared | 3 |
| 12 | Steam | planned | Willingness preferences | Search/paginate imported games; save want/open/not-interested per member | 11 |
| 13 | Steam | planned | Refresh and disconnect | Daily/manual sync preserves preferences; failed sync never clears library; disconnect removes imported association/data | 12 |
| 14 | Steam | planned | Common game recommendations | Rank shared owned games using willingness; permit catalog additions for missing free games; show why each is suggested | 4, 10, 13 |
| 15 | Sessions | partial | Session creation and attendance | Released events already support creation, responses, confirmation, cancellation and reminders (v0.0.1–v0.0.5, v0.0.12). Remaining: server/role audience selection and capacity-enforced session behavior; reuse events where suitable. | 4, 14 |
| 16 | Sessions | planned | Waitlist and readiness | Concurrent joins cannot exceed capacity; timed slot offer promotes next eligible member; readiness buttons work | 15 |
| 17 | Sessions | partial | Durable session reminders | Released event notification routes and voice-aware reminders provide a foundation (v0.0.2–v0.0.5). Remaining: verify session-specific reminders, capacity/waitlist changes, preference handling and restart recovery after Day 16. | 16 |
| 18 | Voting | partial | Game-buying poll creation | General group/server polls, audience controls, catalog suggestions and deadlines shipped in v0.0.6–v0.0.9. Remaining: buying-specific minimum-buyer settings and role-based electorate snapshot semantics; audit existing poll storage before extending it. | 4, 11 |
| 19 | Voting | partial | Buying decisions and result calculation | General votes and results shipped in v0.0.6–v0.0.9, with Active / Start a vote / Results in v0.0.12. Remaining: would-buy/already-own/pass per candidate, eligibility guarantees for that model and explicit buying-result/tie rules. | 18 |
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
- Oracle deployment is documented in DEPLOYMENT_RUNBOOK.md; its recorded commit is historical. Verify the current deployed tag, restore procedure and resource budget on Day 30 rather than treating an earlier hosting proposal as current state.
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
