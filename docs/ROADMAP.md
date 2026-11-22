# HappyMix implementation roadmap

Reviewed: 2026-11-20.

Development started: 2026-11-09 (Day 1). Counting calendar days inclusively, November 20 is Day 12 and November 21 is Day 13. Dates use Europe/Kiev.

Reviewed repository tags and the English, Ukrainian and Russian release manifests through `v0.1.4`. GitHub has no separate Release entries; tags and `src/HappyMix.Bot/Releases/v*.json` provide the release history. Basic voting has already been presented; watchlists, series progress, logs and the dashboard redesign have not. v0.0.13 adds My games, Steam linking/import, the group Live tab and Discord command parity; v0.0.14 adds remembered sign-in with Discord sync, My games pictures/sorting/Save and do-not-disturb; v0.0.15 fixes newer Steam pictures, played free games missing from imports and IGDB Steam links; v0.1.1 moves weekly do-not-disturb into the day editor and adds a By date day overview; v0.1.2 adds opt-in Dota 2 and CS2 stats cards on group pages; v0.1.3 adds recaps of games members played together; v0.1.4 adds the weekly summary and the Game features page with the Dota 2 meta search. They are not yet scheduled for a presentation. The two-day catch-up below covers implemented features without scheduling them for development again. Tagged source does not establish the currently deployed version or completion of a live walkthrough.

## Presentation catch-up

Dates use Europe/Kiev. Both presentations are planned; mark each done only after presenting it. Allow about 15–20 minutes each. Use the current behavior described below: later releases supersede some earlier voting controls. The current 31-day development track below is rebased to the implemented results; today is Day 12.

| Date | Focus | Status | Features | Walkthrough / completion criterion | Release sources |
| --- | --- | --- | --- | --- | --- |
| Day 12 — 2026-11-20 (today) | Dashboard, watchlists and votes | planned | New dashboard navigation and Night/Sand themes; group tabs; series/film/anime watchlists and progress; current voting layout | Tour the side panel, themes and group tabs (3 min). In a group tagged Series, Films or Anime, add a catalog title or a vote winner, set Planned/Watching/Watched/Dropped, mark an episode watched, show season rollover and save a resume timestamp; show valid season/episode limits and Finish series (8 min). Briefly revisit familiar voting through Active / Start a vote / Results, title cards, voters and day/week results; show the native Discord poll and chosen-member private thread (5 min). Do not repeat the basic voting introduction. | v0.0.7 watchlists; v0.0.8 search correction; v0.0.9 watchlist validation and current native polls; v0.0.11 redesign; v0.0.12 vote tabs |
| Day 13 — 2026-11-21 (tomorrow) | Community, events, news and logs | planned | Open groups and join requests; member game suggestions and multi-platform search; game news sources; whole-series confirmation; admin Logs | Request membership in an open group and approve it as organizer; suggest and accept a non-Steam game (5 min). Show Patch notes / Announcements / Press articles and explain that newly enabled sources start from now (2 min). Confirm all pending upcoming dates of a recurring event and show the single confirmation message per accepted person (3 min). As server admin, filter Logs by action/user text, expand details, enable Live and export CSV; explain access, masking and 14-day default retention (5 min). | v0.0.9 open groups, game search and logs; v0.0.10 news sources and quieter release logging; v0.0.12 event confirmation |

Preparation: use a group with the appropriate media tag, a catalog-backed series with season data, a vote winner, a recurring event with pending dates and accepted participants, an open group, a news subscription and a server administrator account. Prepare an ordinary member account for join requests and permission checks. Demonstrate Finish series on a disposable watchlist entry. Check Discord posting permissions, private-thread permissions and configured catalog credentials before the session; film search requires TMDB configuration and multi-platform game search requires Twitch/IGDB credentials. Check the deployed build supports these features before presenting. Keep any unrelated local UI edits out of the release claims.

If time is tight, keep the watchlist progress demo today and the Logs demo tomorrow; shorten the navigation tour, voting recap and game-search examples. Carry unfinished presentation items forward explicitly. Earlier notification and invitation changes in v0.0.2–v0.0.5 stay available as recap material, rather than assuming they are newly presented.

## How to maintain this plan

Edit the tables below, then run `python tools/build_progress.py` from the repository root.
Open `docs/progress.html` directly in a browser. No server, npm install, or paid hosting is needed.
Commit the Markdown and generated HTML together. The dashboard is read-only; it never silently saves changes in browser storage.

Statuses: `done`, `partial`, `planned`, `blocked`. Done means the acceptance criterion is implemented and verified; partial means some implementation exists. Record evidence when changing status.
Development days count from November 9. The track now has 13 implemented milestones and 18 remaining days with partial or planned scope. Future day assignments are adjustable. Presentation dates above do not change implementation status.
Progress is counted by completed items, not estimated hours. Baseline capabilities and completed development milestones are counted separately. Elapsed days alone do not mark a feature done.

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
| B15 | Players | done | My games, Live and Discord command parity | v0.0.13: Want/Can/Won't marks, group interest and vote ranking; opt-in Live tab with DM/channel notices from Discord presence (config-gated) or verified Steam; `/games`, `/watch`, `/availability who`, event/group/news commands and DM buttons. Live delivery against real Discord presence not yet verified |
| B16 | Players | done | Sign-in, My games polish and do not disturb | v0.0.14: sign-in page with Keep me signed in (30 days), saved Discord profile and Sync with Discord; Steam pictures, sorting and a Save button on My games; weekly/dated do-not-disturb holding personal messages with a relevance check on release, shown on boards and `/availability who`. Real Discord sign-in and held delivery not yet verified live |
| B17 | Players | done | Dota 2 and CS2 stats cards | v0.1.2: opt-in sharing per Discord account; group Stats tab with OpenDota medal, 30-day record and top heroes, FACEIT level/Elo/K/D/maps (needs `Faceit__ApiKey`), profile links; refresh job and `/games stats`. Real OpenDota/FACEIT answers not yet verified live (OpenDota match endpoints were erroring during development). See docs/design/GAME_STATS_PLAN.md |
| B18 | Players | done | Games together (match recaps) | v0.1.3: recent matches of sharing members (OpenDota recentMatches, FACEIT history + match stats, kept 30 days); Stats tab lists matches two or more members played together; optional organizer channel posts, each match once and only within a day. Not yet verified against live OpenDota/FACEIT |
| B19 | Players | done | Weekly summary and Game features | v0.1.4: Stats tab last-7-days summary and optional Monday post; Game features page with own stats (synced with Stats tabs), refresh buttons and logged failure reasons; Dota 2 meta search by patch/role/rank/order from STRATZ (current patch dated from OpenDota) with OpenDota lane fallback, admin channel, Post now and daily/two-day/weekly schedule, 5–20 heroes. Discord posting not yet verified live |

## Development checklist

Days 1–12 summarize the scope implemented by November 20, regrouped from the released features rather than asserting an exact daily commit history. Days 13–31 retain the remaining scope. Presentation completion is tracked separately above; live deployment and recovery checks remain explicit future work.

| Day | Area | Status | Deliverable | Acceptance criterion | Depends on |
| --- | --- | --- | --- | --- | --- |
| 1 | Reliability | done | SQLite and MediatR foundation | Startup migrations, persistent announcement history, application command and query handlers, and architecture tests. | B02 |
| 2 | Operations | done | Oracle deployment and HTTPS | ARM64 images deploy through GitHub Actions and GHCR over Tailscale. Caddy served the dashboard through Duck DNS with a valid certificate. Recovery and spending checks remain on Day 30. | 1 |
| 3 | Settings | done | Server settings and esports delivery | Discord and web settings share persisted preferences. CS2 and Dota 2 have separate routing, team catalogs, reminders, and Twitch preferences. Web announcement selectors accept standard text channels. | 1 |
| 4 | Groups | done | Groups, membership, and game attachments | Invite-only groups, descriptions, rosters, role repair, and the web workspace. Games can be attached with an optional Steam App ID. | 3 |
| 5 | Availability | done | Personal and recurring availability | Today and weekly timetable entry, named time zones, fixed offsets, overnight windows, and full-day shortcuts. MVC dashboard and English, Ukrainian, and Russian resources. | 2, 3 |
| 6 | Availability | done | Group member availability board | Seven day tabs, member rows with No data, and distinct one-off and recurring hours with overnight carryover. Authorization and schedule behavior covered by tests. | 4, 5 |
| 7 | Identity | done | Linked accounts and shared schedules | v0.0.1: primary/alternate consent, conflict previews, shared availability and group membership, unlink restore, and per-account Discord permissions. | 4, 6 |
| 8 | Events and notifications | done | Recurring events, invitations and notification routes | v0.0.1–v0.0.5: group/standalone events, series acceptance and decline, per-date withdrawal, confirmation/cancellation, cleanup, inherited notification routes, voice-aware reminders and localized Twitch discovery. Live recovery checks remain on Days 25 and 29. | 3, 7 |
| 9 | Game news | done | Subscriptions, delivery and source controls | v0.0.1–v0.0.5 and v0.0.10: create/join/delete news subscriptions, category-aware channels and roles, paginated Steam feeds, per-post history, Patch notes / Announcements / Press articles and no backfill for newly enabled sources. Live role and cleanup checks remain on Day 29. | 3, 7 |
| 10 | Voting | done | Tags, catalog search and Discord polls | v0.0.6–v0.0.9: group/server votes, deadlines, catalog suggestions and cards, native Discord polls, private threads for selected members, voter lists and day/week results. Buying-specific work remains on Days 19–21. | 4, 8 |
| 11 | Media | done | Watchlists and series progress | v0.0.7 and v0.0.9: add catalog titles or vote winners; Planned/Watching/Watched/Dropped; next episode, season rollover, resume timestamps, valid season/episode limits and Finish series. Not yet presented: demo on Day 12. | 10 |
| 12 | Dashboard and community | done | Redesign, admin Logs, open groups and latest UX | v0.0.9–v0.0.12: Night/Sand themes, side panel, group tabs and icons; server-scoped admin Logs with Live/CSV/retention; open-group requests, game suggestions and multi-platform search; vote sub-tabs and whole-series event confirmation. Implemented scope is complete; presentation is split across Days 12–13 above. | 8, 9, 10, 11 |
| 13 | Availability | partial | Exceptions, opt-in prompts and shared windows | Existing dated/weekly availability and definite/tentative hours provide the foundation. v0.0.14 adds do-not-disturb (weekly and dated) that holds personal messages until it ends; v0.1.1 edits it in the weekly day editor and shows the picked day's free hours, do-not-disturb and events together. Remaining: explicit exception precedence, weekly confirmed-versus-expected overview and shared windows of a requested duration. Keep new implementation light during the second catch-up presentation. | 6, 12 |
| 14 | Steam | done | Profile linking and library import | v0.0.13: link by Steam sign-in, Discord connection or pasted ID/profile URL (vanity names resolved); pasted links are labelled unverified; private libraries and a missing API key show clear errors. | 3, 12 |
| 15 | Steam | partial | Willingness, refresh and disconnect | v0.0.13: Want/Can/Won't marks on web and `/games`; manual re-import keeps marks and never clears on failure; unlink removes imported games. v0.0.15: import adds recently played games Steam omits from the owned list (e.g. Dota 2). Remaining: daily automatic refresh and search/pagination for large libraries. | 14 |
| 16 | Steam | partial | Common game recommendations | v0.0.13: "Popular with members" on group pages and vote suggestions ranked by members' Want marks. Remaining: ranking by shared ownership and explaining each suggestion. | 4, 13, 15 |
| 17 | Sessions | partial | Capacity, attendance, waitlist and readiness | Released events provide scheduling and responses. Add server/role audiences, capacity enforcement on every interaction, concurrency-safe joins, timed promotion of the next eligible waitlisted member and readiness controls. | 8, 16 |
| 18 | Sessions | partial | Durable session reminders | Reuse released event notification routes and voice-aware reminders. Cover session preferences, capacity/waitlist changes, time zones, restart recovery and cancellation suppression. | 17 |
| 19 | Voting | partial | Buying poll creation | Reuse released polls, catalogs, audiences and deadlines. Add minimum buyers and verify role-based electorate snapshots and buying-specific eligibility. | 10, 14 |
| 20 | Voting | partial | Buying decisions and results | Add would-buy/already-own/pass per candidate, changes before deadline, eligible one-vote-per-candidate rules and explicit buying-result/tie calculation. | 19 |
| 21 | Voting | planned | Vote-to-group workflow | Close buying poll durably, retain summary, create a group and propose its first session; use links instead of assuming universal prices. Adding a winner to a watchlist already exists. | 17, 20 |
| 22 | Groups | partial | Temporary roles and text spaces | Persistent group roles already exist. Add opt-in temporary bot-owned roles/text channels, scoped access, creation limits, organizer controls and recovery from partial resource creation. | 4, 21 |
| 23 | Groups | planned | Temporary voice spaces | Create a room; organizer can rename, lock and set capacity through the bot; permissions remain scoped to that room. | 22 |
| 24 | Reliability | planned | Resource lifecycle and cleanup | Persist bot-owned resource IDs/expiry; extend/close controls; lock text before later deletion; preserve occupied voice rooms and unrelated resources; reconcile after restart. | 23 |
| 25 | Esports | planned | Live routing and announcement recovery | Verify saved per-game channels and exact main-team filters against live CS2/Dota 2 notifications; verify restart deduplication and recovery from provider errors. | 3, 8, 9 |
| 26 | Esports | planned | Schedule changes and spoiler controls | Persist message references, update reschedules/cancellations, track cross-midnight matches and suppress/hide spoilers per channel setting. | 1, 25 |
| 27 | Esports | planned | Watch parties | Match notifications create a group/session with attendance, participant reminders and optional temporary room. | 18, 24, 26 |
| 28 | Notifications | planned | Digest and targeted invitations | Weekly sessions/esports/availability digest; game-interest opt-in invitations; snooze, quiet hours and mention limits. | 13, 18, 27 |
| 29 | Quality | planned | End-to-end pilot and recovery checks | Exercise availability -> suggestion -> vote -> group -> session, plus existing account linking, news role/cleanup, watchlist and event flows; verify unauthorized interactions and retries after failures on the deployed build. | 28 |
| 30 | Operations | planned | Backup, restore and resource budget | Restore SQLite to a fresh instance; measure idle/peak memory, provider allowance and spend; document rollback and verify the deployed tag. | 29 |
| 31 | Release | planned | Fixes, walkthrough and next backlog | Resolve pilot blockers; document commands and permissions; review acceptance criteria; explicitly carry incomplete scope forward. | 30 |

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
- Advanced availability poll voting. Recurring events are included in Day 8.
- Match prediction leaderboard without money or betting.
- Regional price alerts from a verified permitted data source.
- Calendar export and optional external calendar synchronization.
- Game stats Phase 4 (docs/design/GAME_STATS_PLAN.md): ranks on the Live tab; more CS2 features (Premier).

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
- Announcement state is now persisted per destination channel and discipline. Guild-specific notification preferences are implemented; live routing verification remains on Day 25.
- Existing local changes were present during this review; this roadmap does not certify them as released.
- The repository still pins SDK 10.0.102 with latestPatch. Tests were successfully executed with installed SDK 10.0.402 from work/sdk-validation using a local SDK selection file; the root pin is unchanged. Normal root-directory dotnet commands still require the pinned SDK or an explicitly reviewed SDK-policy update.
