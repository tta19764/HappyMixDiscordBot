# HappyMix Discord Bot

A Discord bot for the HappyMix server, built with Discord.Net, .NET 10, and Clean Architecture.

## Implementation progress

Open [the offline progress dashboard](docs/progress.html) in a browser to inspect the
audited baseline, next 30 work slots, acceptance criteria, and dependencies.
Filter by area or status, or search for a feature.

[docs/ROADMAP.md](docs/ROADMAP.md) is the source of truth. Update its statuses and
evidence as implementation proceeds, then regenerate the dashboard:

```powershell
python tools/build_progress.py
python tools/build_progress.py --check
```

The generator uses only Python's standard library. Commit the Markdown and HTML
together. The dashboard works directly from disk without a web server or frontend
build. Progress describes reviewed implementation, not live deployment health.

Quartz.NET polls PandaScore every minute for followed main-team CS2 and Dota 2 series.
MediatR dispatches requests from the Discord and Quartz adapters to Application handlers.

## Followed esports teams

| Discipline | Main rosters | PandaScore team IDs |
| --- | --- | --- |
| CS2 | NAVI, FUT Esports, Monte, B8, Inner Circle Esports, G2 | 3216, 136955, 131441, 129381, 136497, 3210 |
| Dota 2 | NAVI, Monte | 1699, 132557 |

Team IDs were verified against PandaScore on 2026-11-09. No FUT Dota 2 main
roster was returned. Academy/junior teams and similarly named teams are not
followed. A junior team may still appear as an opponent of a followed main team.
This configuration follows exact roster identities, not player nationalities.

The new default is **only followed main teams at all tiers**, replacing the old
general S/A-tier CS2 feed. This includes qualifiers and unknown tournament tiers.
Set `PandaScore:IncludedTournamentTiers` to `["s", "a"]` to additionally restrict
followed matches to those tiers; leave it empty for all tiers. Overrides in local
development settings or environment variables can retain an older tier restriction.

The bot follows the IDs in the configured team alias maps by default.
`PandaScore:CounterStrikeTeamIds` and `PandaScore:Dota2TeamIds` can optionally
override those defaults. An explicit empty list disables that discipline.
Verify replacement IDs with PandaScore before changing them.

Schedules are paginated by discipline and tournament. Notifications include
starting-within-30-minutes alerts, CS2 map/Dota game results, and series results.
The schedule date uses `PandaScore:TimeZoneId` (default `Europe/Kyiv`); Discord
timestamps display in each viewer's timezone. Running matches are fetched across
date boundaries, and completed matches are selected by completion date.

Requests are paginated rather than silently capped at 100 records. Provider
failures abort the synchronization instead of publishing an incomplete empty feed.
Announcement history is stored in SQLite, scoped to destination channel,
discipline, event kind and provider ID. Recorded publications survive restarts.
Reschedule-message edits and per-guild subscriptions remain future work.

## SQLite persistence

Startup creates the data directory and applies versioned EF Core migrations before
connecting Discord or starting Quartz. An invalid path or failed migration stops
startup; the bot does not silently fall back to memory.

```json
"Storage": {
  "DatabasePath": "data/happymix.db",
  "ReplayExistingResultsOnFirstRun": false
}
```

The local path is relative to the process working directory. Override it with
`Storage__DatabasePath` for an absolute deployment path. Keep the same file and
announcement-channel ID across restarts. Each database operation uses a new EF
context; run only one publishing bot instance against this file.

On the **first successful synchronization for a new database/channel**, the default
policy records already completed series and maps/games as historical, without
posting their results. Today's schedule and upcoming reminders can still post.
This avoids replaying all available results when moving from the old in-memory
implementation. Set `ReplayExistingResultsOnFirstRun=true` before the first sync
to intentionally replay them. Changing the flag later does not reset history.
The old process's memory cannot be imported, so the daily schedule or a still-upcoming
reminder may be posted once on the first SQLite rollout.

Delivery is not exactly once: if Discord accepts a message and the process crashes
before the marker commits, that message may be sent again. Failed sends are not
marked as delivered. Multi-page schedules can replay earlier pages if a later page
fails. Unique database keys prevent duplicate marker rows, not duplicate sends
from multiple running bot instances.

For an offline backup, stop the bot cleanly before copying the data directory.
For online backups use SQLite's backup API, not a raw copy of an active database.
Restore while the bot is stopped and retain the channel configuration. Automated
backup/restore operations remain Day 29 work.

See [Day 1 implementation and verification](docs/SQLITE_DAY1.md).

See [the implementation plan and verified identities](docs/ESPORTS_IMPLEMENTATION.md).
API references: [Dota 2 upcoming matches](https://developers.pandascore.co/reference/get_dota2_matches_upcoming),
[team matches](https://developers.pandascore.co/reference/get_teams_teamidorslug_matches).

## Project structure

- `src/HappyMix.Domain` contains enterprise rules and domain models. It has no project dependencies.
- `src/HappyMix.Application` contains use cases and abstractions. It depends only on Domain.
- `src/HappyMix.Infrastructure` implements Application abstractions for external concerns.
- `src/HappyMix.Bot` is the Discord.Net adapter and composition root.
- `tests/HappyMix.Application.Tests` contains application unit tests.

## Run locally

The `HappyMix.Bot` Rider configuration and default `dotnet run` launch profile set
`DOTNET_ENVIRONMENT=Development`, which loads `src/HappyMix.Bot/appsettings.Development.json`
on top of `appsettings.json`. Add your local configuration to that ignored file;
environment variables override values from both files.

Create a bot in the Discord Developer Portal, then provide its token through configuration:

```powershell
$env:Discord__Token = "your-bot-token"
$env:Discord__AnnouncementChannelId = "your-channel-id"
$env:PandaScore__Token = "your-pandascore-token"
dotnet run --project src/HappyMix.Bot
```

The bot registers a global `/ping` slash command after connecting. Global command changes can take time to propagate in Discord.

Never commit the bot token. For local development, environment variables or .NET user secrets are recommended.

## Server settings

Discord server administrators can manage these SQLite-backed settings with `/settings`.

| Command | Behavior |
| --- | --- |
| `/settings channel` | Choose the default esports channel or set an override for CS2 or Dota 2 with the optional `game` argument. Select a game and leave the channel blank to clear its override. The bot needs View Channel, Send Messages, and Embed Links permissions. |
| `/settings timezone` | Choose the time zone that defines the daily esports schedule, for example `Europe/Kyiv`. |
| `/settings teams` | Choose a game and enter comma-separated catalog names or PandaScore team IDs. Use `default` to follow the catalog or an explicit deployment override, or `none` to disable that game. |
| `/settings add-team` | Add or update a name and its PandaScore team ID in this server's catalog. |
| `/settings team-catalog` | Browse the server's team catalog by game. |
| `/settings notifications` | Enable or disable schedules, reminders, map/game results, and series results individually. |
| `/settings reminder` | Set the reminder lead time from 1 to 1,440 minutes. The default is 30 minutes. |
| `/settings organizer-role` | Add, remove, or clear roles saved for future group commands. |
| `/settings ping-role` | Choose a role for one game's reminders. Omit the role to clear it. The role must be mentionable or the bot must have permission to mention it. |
| `/settings twitch` | Set comma-separated Twitch handles within the configured limit or HTTPS channel links for one game. Use `default` to reset or `none` to hide links. |
| `/settings show` | View the settings privately. |

`/settings set` continues to save the channel and time zone together. If a server has no saved announcement channel, the deployment channel remains the fallback for its own server. Reminder mentions are limited to the configured role for that game.

The alias maps in `appsettings.json` seed each catalog on its first settings save. Each server then keeps its own copy in SQLite. Adding a team makes it available for selection. Servers using the catalog as their followed list also begin following the added team. A server with an explicit followed list can select the new team with `/settings teams`.

Daily schedules show **Watch** when PandaScore lists a preferred Twitch channel for the match. Otherwise they show the preferred links as **Twitch channels**. Defaults are `cs2_maincast` and `cs2_maincast2` for CS2, and `ghostik` and `dota2_maincast` for Dota 2.

## Saved gaming groups

Use `/group create name:Friday` to create an invite-only group. The creator can use
`/group invite` to save an invitation and send a DM with **Accept** and **Decline** buttons.
If the DM is blocked, members can find the invitation with `/group list` and respond with
`/group join` or `/group decline`. Members can leave with `/group leave`. Use `/help` for an overview of bot commands.

The organizer and members with Manage Server or Administrator can invite and remove
members, revoke invitations, or delete the group. The organizer can close the group
with `/group delete`. When organizer roles are configured, group creation requires
one of those roles or Manage Server or Administrator permission.

## Run with Docker

For Oracle Always Free hosting and GitHub Actions deployment, follow the
[step-by-step setup plan](docs/ORACLE_CICD_PLAN.md). This is preparation for Day 2,
not a claim that production is deployed.

The production Compose file, deployment scripts and GitHub workflow are now available.
Follow [the first-run deployment runbook](docs/DEPLOYMENT_RUNBOOK.md). Deployments remain
disabled until the repository variable `ENABLE_PRODUCTION_DEPLOYMENT` is set to `true`.

For development, use the `HappyMix.Bot Docker (Development)` Rider configuration or run:

```powershell
docker compose -f compose.development.yaml up --build
```

This sets `DOTNET_ENVIRONMENT=Development` and mounts
`src/HappyMix.Bot/appsettings.Development.json` read-only into the container.
The file must exist before starting. Development settings are excluded from published output
and the Docker build context.

Compose mounts the `happymix-data` named volume at `/app/data`. The image creates
that directory with ownership for its non-root `app` user. Normal container
recreation preserves the database; `docker compose down -v` deletes the volume and
its history. For a bind mount, grant the runtime user write access to the directory.

Build the production image from the repository root:

```powershell
docker build --tag happymix-bot .
```

Provide the Discord token, announcement channel, and PandaScore token when the container starts:

```powershell
docker run --rm --name happymix-bot `
  --mount source=happymix-data,target=/app/data `
  --env Discord__Token="your-bot-token" `
  --env Discord__AnnouncementChannelId="your-channel-id" `
  --env PandaScore__Token="your-pandascore-token" `
  happymix-bot
```


### One-off availability

Use `/availability add` or `/availability edit` with local dates and times for both endpoints,
for example `start:2026-12-01 22:00 end:2026-12-02 01:00 timezone:Europe/Kyiv`.
Choose the required `timezone` from autocomplete suggestions. Times skipped or repeated by
daylight saving are rejected. Discord displays saved times
in each viewer's local timezone. Use `/availability list` to get the ID for `edit` or `remove`.

For `/availability set today` and `/availability set timetable`, enter local times such as
`18:30` and `22:00`, or select `all_day`. The optional `timezone` offers named suggestions
and defaults to the server time zone. An earlier end time means the following day.
Use `tentative` for uncertain availability.

### Live match alerts and Twitch links

The bot posts a start alert the first time PandaScore reports a followed series as running.
Use the `match-starts` option of `/settings notifications` to control these alerts.
Set `Twitch__ClientId` and `Twitch__ClientSecret` in the deployment environment
(`/opt/happymix/bot.env` on the VM) using credentials from a Twitch developer application.
Reminders (using the configured lead time), match-start alerts, and results after each map
link only preferred channels that Twitch reports live in the matching game category
when the bot sends the message. A live channel can be showing a different match.
These notifications still post without Twitch links if credentials are missing or the lookup fails.

## Availability dashboard

The ASP.NET Core MVC dashboard runs in the bot container with the same SQLite volume.
Both Compose configurations publish port 8080 on host loopback. The dashboard
is disabled until configured.

Set these values in the production bot environment file (or the local development
configuration, with the secret kept out of source control):

```dotenv
Dashboard__Enabled=true
Dashboard__ClientId=your-discord-application-id
Dashboard__ClientSecret=your-discord-oauth-client-secret
Dashboard__PublicUrl=https://happymix.example.com
Dashboard__TrustedProxy=172.18.0.1
```

Use the actual IP of the reverse proxy as seen by Kestrel. The IP above is an
example. Terminate HTTPS at the proxy, forward requests to host loopback port
8080, preserve the public Host header, and set X-Forwarded-Proto to https.
Register the exact redirect URI https://happymix.example.com/signin-discord in
the Discord application's OAuth2 settings. The bot token is separate from the
OAuth client secret.

Recreate the Compose service after updating its environment. Open the HTTPS URL
or use /availability dashboard in Discord. For local OAuth testing, register
http://localhost:8080/signin-discord and open http://localhost:8080 directly.

After signing in, choose **Server settings** next to your server. The same
Discord Administrator permission required by /settings is checked for both
viewing and saving. Changes affect that server's saved settings immediately.
Bot credentials and deployment defaults remain in the host configuration.

### Dashboard localization

The dashboard uses matching stable resource keys in `DashboardResources.en.resx`,
`DashboardResources.uk.resx`, and `DashboardResources.ru.resx` under
`src/HappyMix.Bot/Resources/Web`. English is the neutral fallback language, stored
in its satellite resource assembly. Keep keys stable when editing visible wording,
and preserve format placeholders such as `{0}` across languages.

Use `T["Resource_Key"]` in views and `text["Resource_Key"]` in controllers.
`DashboardLocalization.Message` adapts English messages supplied by application
errors, Discord command metadata, and existing view models to the resource keys.
Unknown messages retain their original text. New dashboard copy should use direct
stable-key lookups and an entry in each language file.

### Steam game news and Discord commands

Each news subscription chooses its kinds of news (`GameNews.Sources` flags, migration `20261129000100_AddGameNewsSources`): patch notes (`steam_community_announcements` posts tagged `patchnotes`), other announcements from the Steam page, and press articles (every other feed). New and existing subscriptions post patch notes and announcements; the creator or an administrator changes this on the game's card. Newly ticked kinds start from that moment: posts already out are recorded as delivered. The reader paginates back to the subscription creation time and retains per-post delivery deduplication. Newly created news and release channels are placed in the `Bot Notifications` category. Existing channels retain their locations.

The current implementation roadmap and November 20–21 presentation catch-up are in [docs/ROADMAP.md](docs/ROADMAP.md). The historical v0.0.2 implementation plan is in [docs/V0_0_2_PLAN.md](docs/V0_0_2_PLAN.md).

Use the dashboard's **Game news** tab or `/game-news create` with a Steam App ID or
store link and a display name. HappyMix creates one channel and role per game per
server. Any member can join or leave the role. The creator and administrators can
delete the subscription and its Discord resources. The bot needs Manage Channels,
Manage Roles, and the ability to mention its non-mentionable subscription role
(the Mention Everyone permission in the news channel). Its role must be above the
roles it manages. Notifications whitelist only the subscription role.

Every ten minutes HappyMix checks Steam announcements, skips posts from before the
subscription, and records delivered IDs in SQLite. Release, playtest and update
headings are inferred from announcement titles. Every post links to the source.
Silent builds and unannounced store or playtest state changes are outside this feed.

`/dashboard` opens the dashboard. Use `/event list`, `/event groups` and `/event show`
to find IDs and dates, then `/event create`, `/event respond`, `/event confirm` or
`/event cancel` to manage events in Discord.

### Settings list limits

Set `Discord:MaxFollowedTeamsPerGame` and `Discord:MaxTwitchChannelsPerGame` in
`appsettings.json` (defaults 20 and 5). Environment overrides use
`Discord__MaxFollowedTeamsPerGame` and `Discord__MaxTwitchChannelsPerGame`.
Both values must be positive. Restart the bot after changing them. Dashboard list
controls and server validation, plus slash commands, use these settings. Existing
saved lists remain intact. The dashboard also enforces text-input lengths of
4,000 characters for teams and 1,000 for Twitch channels.

### Feature guides and releases

The Info page explains availability, vacations, weekly copying, groups, events,
Steam subscriptions, linked accounts and settings in English, Ukrainian and Russian.
Each server tab links to its guide section; /help links to the full guide.

Version tags trigger tested production deployments. After health verification,
HappyMix publishes Ukrainian notes in bot-releases on server 887398817404694548.
The channel is reused or created automatically. See CONTRIBUTING.md for feature/fix
branches, release-note files and the release process.

### v0.0.2 notification workflows

- In each game's settings, choose an existing announcement channel or create one. `/settings create-channel` provides the same action.
- CS2 and Dota 2 notification languages are independent. `/settings broadcasts` configures language and optional live match discovery. Language, country tags, other tags and viewer count can be enabled by including them in the priority list. Values within each preference list are ordered. Country tags are broadcaster-supplied, not geolocation.
- Twitch discovery examines at most `Twitch:DiscoveryMaxPages` pages of 100 live streams (default 10) and returns up to `Twitch:DiscoveryMaxResults` links (default 5). Discovery is best effort: both teams must be identifiable in the title. Chosen streamers are checked separately. Credentials remain required.
- Accept a recurring event once to join its future dates. A dated decline/withdrawal overrides only that date. Earlier date-specific responses remain explicit overrides. Organizers still confirm each occurrence separately.
- Group and event detail pages link to **Channel notifications**. Choose/create a channel, enable reminders, and select a language. Event settings override group defaults; select inheritance to reset them. `/notify set` and `/notify show` expose the same configuration to organizers and administrators.
- Confirmed-event reminders run every minute, `EventNotifications:ReminderMinutes` before start (default 15), with a five-minute late recovery window. Mentions include accepted participants and the organizer only when absent from all guild voice channels. Linked alternate accounts count as voice presence. Delivery receipts persist across restarts; footer markers recover recent sends if a crash occurs before recording the receipt. This is not a guarantee of exactly-once Discord delivery.
- All newly created text channels use **Bot Notifications**. Existing channels retain their location. The bot needs Manage Channels to create them and View Channel, Send Messages, and Embed Links to post. Voice-state gateway intent is enabled; this does not require the privileged member intent.

API references: [Steam news pagination](https://partner.steamgames.com/doc/webapi/ISteamNews), [Twitch stream metadata](https://dev.twitch.tv/docs/api/reference/streams).

### v0.0.4 quality-of-life updates

- **Community defaults.** `CommunityDefaults` in configuration sets the starting language and Twitch preferences for servers that haven't chosen their own. The built-in values target Ukrainian users: notifications in Ukrainian, stream language `uk`, country tags `Ukraine,UA,Україна` and tags `Ukrainian,Українська,УкраїнськоюМовою`. Saved choices are never replaced; settings saved by v0.0.2/v0.0.3 keep their explicit language.
- **Match posts per channel.** Titles, footers, schedule labels and stream lines now use each game's language, so a shared channel never mixes English headings into Ukrainian posts. An empty day posts one note per language, naming only the games routed to that channel. Discovered broadcasts now appear on their own line (the v0.0.2 discovery setting was saved but not yet used in posts).
- **Settings page.** "Where match posts go" lists each channel with its games and languages, warns about mixed languages or a channel named for the other game (for example `#cs2-tournaments` receiving Dota 2), and offers a one-click own channel. Language can follow the community default; live broadcast options open only when enabled, can follow the community lists, and ranking criteria are ticked and ordered instead of typed.
- **Optional command options.** `/settings broadcasts` and `/notify set` change only the options supplied. In list options `-` clears a list and `default` returns to the community lists; `/settings broadcasts` with only a game shows the current values. `/notify` targets come from autocomplete instead of pasted IDs.
- **Group and event channels.** Notification pages show where settings come from (own, group, not set up) with a message preview. Routes gain a per-route reminder lead time and an opt-in for updates when events are scheduled, dates are confirmed or canceled, or a series is canceled (changes from the last six hours only, each posted once). Created channels are named after the group or event, reminders show the start time, attendee count and a dashboard link, and footers carry a short reference instead of the internal delivery key. Migration `20261123000100_AddNotificationRouteOptions` adds the columns; existing routes stay reminder-only.

### v0.0.5 event invitations and canceled events

- Repeating events send each invitee one invitation (for the next date inside the three-day window) with **Accept all dates** / **Decline all dates**. A series decline is stored (`EventInvitees.SeriesDeclinedUtc`, migration `20261124000100_AddSeriesDecline`) and stops further invitations; accepting the series again clears earlier per-date declines of future dates. Single dates can still be skipped on the event page. One-off events keep per-date invitations.
- Canceled events and dates are hidden from event, group and availability lists immediately and permanently deleted `EventRules.CanceledRetention` (15 minutes) after cancellation, once no messages for them are waiting. A canceled series is deleted with all its dates, answers, messages and its event notification route. Earlier cancellations are cleaned up on the first run.

### v0.0.6 group tags and votes

- Groups have up to 8 tags (Settings section). Built-in `games`, `series`, `films` and `anime` decide vote suggestions; other tags are labels. Tables `GroupTags`, `Polls`, `PollOptions`, `PollAudience` and `PollVotes` come from migration `20261125000100_AddGroupTagsAndPolls`.
- Group votes are open to members who can see the roster. Server votes (Votes tab) go to chosen members (2–100 including the creator) or everyone on the server and are listed only to them.
- Options are titles or Steam/IMDb/MyAnimeList/AniList links; Steam and MyAnimeList slugs fill in titles, IMDb links need a title. Suggestions combine the group's games (games tag) with options from the last 30 votes, least recently won first.
- Votes close at the chosen time (1 hour to 1 week) or earlier by their creator or the group's organizer/moderators. Winners are the most-voted option, ties going to the first listed.

### v0.0.7 votes in Discord, media details and watchlists

- Votes with a channel are posted as a Discord message: a summary embed with vote bars, a card per catalog-backed option (poster, year, genres, rating, seasons/episodes or running time, watchlist progress), a select menu to vote and a button to take the vote back. The message updates after every vote (Discord or dashboard) and shows the winner once voting ends; `PublishPollsJob` catches up every minute. Group votes use the group's notification channel when notifications are on; server votes use the channel chosen in the form or the channel of `/vote create`.
- `IMediaCatalog` (Infrastructure `MediaCatalog`) looks titles up in the Steam store, TVmaze (series, IMDb lookups), AniList (anime; MyAnimeList links by id) and, with `Media__TmdbApiKey`, TMDB (films). Lookups are best effort with a 6-second HTTP timeout (4 seconds while creating a vote) and cached for 7 days in `MediaCache`. Only the typed search text and link ids are sent to these services.
- Groups tagged series, films or anime have a watchlist (`WatchItems`): status, next season/episode (rolling over by catalog season lengths), and a resume position for unfinished episodes or films. The organizer and moderators edit it; members see it. Migration `20261126000100_AddDiscordVotesAndWatchlist` adds the tables and columns.

### v0.0.8 Discord polls and title cards

- Plain votes (no catalog-backed options, every answer at most 55 characters, for a group or everyone) are posted as native Discord polls (duration rounded up to whole hours, max 768). Votes cast in Discord are copied live from `PollVoteAdded`/`PollVoteRemoved` gateway events (intent `GuildMessagePolls`, not privileged) and resynced every minute and when the poll ends; ending a vote early ends the Discord poll. Dashboard voting on these polls links to Discord instead.
- Votes with catalog titles, or for chosen members, are posted with Discord's component layout (Components V2): one section per option with a poster thumbnail, linked title, preset details, watch progress and vote bar, a vote menu, and the winner with a large poster once voting ends, kept within Discord's 40-component limit. Older embed messages fall back to embeds when Discord refuses the conversion.
- Discord.Net is 3.20.2. Catalog parsing treats null numbers as unknown and skips single bad search entries instead of failing the whole search.

### v0.0.9 admin Logs tab and open groups

- A Logs tab for each server's Discord administrators shows that server's entries; bot owners listed in `Logs:OwnerIds` can also include bot-wide entries. Filters: level, area (actions, Discord, scheduled, dashboard, integrations, system), period and text; entries expand to show source, details and errors; Live adds new entries every 10 seconds; filtered entries download as CSV (formula-safe).
- `DatabaseLoggerProvider` stores HappyMix entries from Information and framework entries from Warning in `AppLogs` (migration `20261128000100_AddAppLogs`) through a bounded in-memory queue and `DatabaseLogWriter` batches; `Logs:RetentionDays` (14) and `Logs:MaxEntries` (50,000) prune hourly. Tokens, keys, passwords and bearer values are masked; what people type isn't logged.
- `CommandLoggingBehavior` logs every command with server, user, outcome and duration (rejections as warnings with the error code); minute-by-minute background commands log at Debug. Discord commands, buttons, server joins/leaves, disconnects, vote posts and per-server publishing failures are logged with their server.
- Every vote posted to Discord is a native Discord poll (answers shortened to 55 characters). Title votes get a static Components V2 info card above the poll; votes for chosen members go into a private thread with just them (needs Create Private Threads and Send Messages in Threads; otherwise the vote stays on the dashboard and a warning is logged). The dashboard lists voters per option and shows ended votes in full for today and this week in the server time zone; the Votes tab defaults to everyone.
- Watchlist entries backed by catalog seasons only accept existing seasons and episodes (season picker with episode counts, server-side validation); **Finish series** marks the last episode watched.
- `System.Net.Http.HttpClient` console logging is raised to Warning so request URLs (which can carry API keys) are no longer printed.
- Groups can be open or invitation only (stored in the group JSON with pending join requests and game suggestions; no migration). Open groups appear for everyone under **Open to join**; their roster stays members-only. Requests go to the organizer, who approves or declines them on the group page (moderators can too); an existing invitation joins directly; closing a group drops pending requests. The organizer, requester and suggester get Discord DMs in the community language through `IGroupNotifier` (best effort).
- Members suggest games for the group; the organizer adds or dismisses each suggestion. Join and suggestion actions are dashboard-only for now.
- Games and game votes search IGDB when `Twitch:ClientId` and `Twitch:ClientSecret` are set (otherwise Steam), so non-Steam titles such as League of Legends or Fortnite keep their platforms, cover and official site. A result that is also on Steam is stored as a Steam game so Steam news keeps working. Epic Games Store, GOG and IGDB links are recognised in votes.

### v0.0.10 game news sources

- Each game news subscription chooses patch notes, Steam page announcements and/or press articles (`GameNews.Sources`, migration `20261129000100_AddGameNewsSources`; existing subscriptions keep patch notes and announcements). Newly ticked kinds start from that moment. Discord headings name patch notes or the press site.
- `PublishReleaseNotesJob` stops before calling Discord once every note of the current version is posted, and logs only when it posts.

### v0.0.11 dashboard redesign

- Squared, flat design: 2–4 px corners, no gradients or glows, IBM Plex Sans/Condensed/Mono (Latin and Cyrillic) from Google Fonts. Tokens in section 1 of `dashboard.css`; the redesign layer is section 35.
- Themes: **Night** (dark, warm charcoal) and **Sand** (light: sand page, oat panels, espresso side panel; no white). Section colors stay: Availability green, Events amber, Game news coral, Groups pink, Votes violet, Linked accounts blue, Settings and Logs slate; the layout puts `hm-sec-<section>` on `<body>`.
- The header and server tabs are replaced by a retractable side panel (`_DashboardLayout`): server switcher, sections, admin links, guide, theme, language, account and sign-out. Collapsed state is kept in `localStorage`; on phones it is a bar with a Menu button. Pages report their section through `SideNav` (`_ServerNav` does it for most pages).
- Group pages have a tab row (`data-group-tabs`); with scripts on, one `data-group-panel` shows at a time, chosen by the address hash, so existing `#section` redirects open the right tab. Availability and Members keep their server-side mode (`?tab=roster`).
- `DashboardIcons` is HappyMix's own icon set (24 px grid, square caps). Icons mark navigation, tabs, headings, cards and tags; buttons are words only apart from collapse, switch server, theme, language and sign-out. `DashboardDesignTests` checks icon names and that views use no emoji.

### v0.0.12 vote tabs and series confirmation

- `_Polls` splits into Active, Start a vote and Results panels (`data-tabs` sub-tabs, ids `votes-<group|server>-<tab>`); `PollRedirect` sends people back to `#votes-…-active`, or `-new` after a failed start. Without scripts all three panels show with their headings.
- `ConfirmEventSeriesCommand` (POST `/events/{guild}/{event}/confirm-all`) confirms every pending upcoming date; each person who accepted one gets a single Confirmed message for their first newly confirmed date.
- Event date boxes show weekday, day and month; compact series rows show only the times, with status and count underneath. Side panel menus open beside the panel (`position: fixed`) so its scroll area no longer clips them.

### v0.1.9 vote types and whole matches

- Vote types: `Poll.Purpose` (Choice / Buy) is the type. The New vote tab has a type picker (`.hm-poll-types`, shown with CSS `:has()`; without it both templates show) and one form per type; both share `WhereAndWho` (group channel, or server channel and Everyone / Chosen people). Active and Results have type filter chips (`data-poll-filter`, dashboard.js).
- Buy votes outside groups: `CreateBuyVoteCommand` takes an optional `GroupId`, `Audience` and `Everyone`. For everyone, owners come from `IPlayerGameStore.ListOwnersAsync` and are counted only when `IGuildMemberGateway` says they are in the server; `SyncBuyVotesCommand` follows the same rules (group members, chosen people, or server members). Server boards offer `PollBoard.BuyGames`: games the viewer wants, earlier vote games and the server's group games. `/vote buy` starts one in Discord.
- Whole matches (`MatchDetail`, `MatchSide`, `MatchParticipant`; table `MatchDetails`, migration `20261208000100_AddMatchDetails`, pruned with matches): the recap job reads up to `MatchRecaps.DetailsPerRun` = 8 matches per run that the Stats tab shows, from STRATZ `match(id)` when `Stratz__ApiKey` is set, else OpenDota `matches/{id}`, and FACEIT `matches/{id}/stats` for CS2. Matches without public players are saved without players and not asked again. Members are matched by hero (Dota 2) or by side and K/D/A (CS2) in `MatchRecaps.Members`. Private profiles have no name. Discord recaps add the score, the mode and members' net worth and GPM.

### v0.1.8 buy votes, twink accounts and meta posts

- Buy votes in groups with four fixed answers; Steam owners are counted as Already have on creation and after imports. Up to five Steam accounts per person, with labels, a main account and per-account stats sharing. Dota 2 meta posts with hero emojis, lane or role blocks and STRATZ / Dota2ProTracker links.

### v0.1.7 hourly stats and ranked-only Dota 2 cards

- Shared cards refresh hourly within the OpenDota free budget (`FreeBackgroundCalls`); Refresh me reloads only your own cards. Dota 2 card numbers use ranked matchmaking only, and identical OpenDota/STRATZ requests in flight are shared.

### v0.1.6 menu order, weekly do not disturb, Load more and OpenDota timeouts

- Side panel order (`SideNavOrder`, cookie `hm_nav`, keys joined by dots because a comma splits a cookie): a Reorder switch shows up/down buttons (`POST /nav-order` without scripts) and dashboard.js adds drag and drop; links missing from the saved order keep their default place. The move buttons are icon-only with labels, an exception to the words-only button rule.
- Weekly day editor: each range posts `rangeType` (free / dnd) and `rangeKind` (definite / tentative); `DashboardController.RangeKinds` combines them (older pages that post only `rangeKind` still work). Certainty is hidden for do-not-disturb rows; "Add do not disturb" starts at 23:00.
- My games: `?page=N` shows the first N × 50 games (up to `MaxListed` = 500) with a Load more link; dashboard.js appends the next batch and updates the URL. Save posts only changed rows; the server accepts up to 500.
- OpenDota: 45-second timeout (`OpenDotaSeconds`); the card's calls run in parallel; recentMatches and peers are optional; Turbo is left out of averages.

### v0.1.5 group overview and Dota 2 stats

- Group Overview (`_GroupOverview`): next event with who's going (`EventRow.Going`) and the Events tab's answer buttons; today's free members from the availability board and Live "Playing now"; the latest game together and the last 7 days per game; top games by "Want to play" marks and open votes.
- Dota 2 cards (`DotaProfile.Recent`, `Roles`, `Peers`, `RankHistory`): OpenDota `recentMatches` (last 20: form, streak, K/D/A, GPM/XPM, solo vs party, ranked results) and `peers`; positions 1–5 from STRATZ `player.matches` (50 matches) when `Stratz__ApiKey` is set, else OpenDota lanes. The medal is saved once a day inside the cached card for 90 days (no migration) to show its change; the MMR estimate is ranked wins minus losses × 25. Group Stats tab: games with the group (14 days, from recaps), most games with, and Duos. `/games stats` gets a second Dota 2 line.
- Game features: game list (`/game-features/{guild}`, cards / table / list with search, view kept in a cookie) and a page per game (`/game-features/{guild}/{game}`) with tabs. The meta search runs on Search only; minimum pick rate filter (default 1%, migration `20261206000100_AddMetaMinPickRate` for posts) and a meta score explanation.

### v0.1.4 weekly summary and Game features

- Weekly summary (`WeeklyDigestRequests`, migration `20261205000100_AddWeeklyDigest`): "Last 7 days" on the Stats tab (games together, record, most played hero/map, best performer with 3+ games) and an organizer switch for a Monday 10:00 post of last week (`post-weekly-digests`, hourly, once per group and week).
- Game features page (`/game-features/{guild}`, migration `20261205000200_AddGameFeatures`): your stats with the same switch as Stats tabs, Refresh buttons (`RefreshMyStatsCommand`, `RefreshGroupStatsCommand`), and failure reasons stored with the card (`GameStatsCache.Reason`) and logged with UserId (and GuildId when started from a server page).
- Dota 2 meta: STRATZ search (`Stratz__ApiKey`; `heroStats.winDay`/`winWeek`, ranked All Pick, positions, rank brackets; current patch dated from OpenDota `constants/patch`; Wilson-score order; one cached request per filter for an hour). Without STRATZ: OpenDota `scenarios/laneRoles` by lane. Admins choose or create `#dota-meta`, Post now, and a daily / every-two-days / weekly schedule (`post-dota-meta`, hourly) with saved filters and 5–20 heroes per role.

### v0.1.3 games together

- Match recaps (`MatchRecapRequests`, migration `20261204000100_AddMatchRecaps`): `refresh-matches` (every 10 minutes) reads each sharing member's history at most every 30 minutes (OpenDota `recentMatches`; FACEIT `history` plus `matches/{id}/stats` for new matches), keeping matches 30 days in `PlayerMatches`.
- A match saved for two or more sharing members of a group is a recap: listed under "Recent games together" on the Stats tab (14 days) and, when the organizer turns it on (`RecapChannels`), posted once in the group's notification route (`DiscordRecapPublisher`), only for matches from the last day. Handled recaps are recorded in `GroupRecaps` even while posting is off.
- Turning stats sharing off also deletes the person's saved matches.

### v0.1.2 Dota 2 and CS2 stats

- Group **Stats** tab for groups whose games include Dota 2 (Steam 570) or CS2 (730): a card per member from OpenDota (medal, 30-day wins/losses, top heroes of 90 days) and FACEIT (level, Elo, matches, win rate, K/D, headshots, top maps), with OpenDota/Dotabuff/FACEIT/Leetify/Steam links. Plan and later phases: `docs/design/GAME_STATS_PLAN.md`.
- Opt-in per Discord account (`GameStatsSharing`, migration `20261203000100_AddGameStats`), off by default; turning it off deletes the cache (`GameStatsCache`). Sharing fetches at once; `refresh-game-stats` (every 10 minutes) refreshes cards older than 6 hours, 20 people per run. A failed lookup keeps the last good data.
- CS2 needs `Faceit__ApiKey`; `OpenDota__ApiKey` is optional. Discord: `/games stats [member]` and `/games share-stats`.

### v0.1.1 do not disturb in the schedule

- Weekly do-not-disturb is edited in the day editor: each range's kind is Definite, Tentative or Do not disturb (`rangeKind=dnd`). Saving splits the day into free hours (`SetWeeklyDayCommand`, this server) and do-not-disturb (`SetQuietDayCommand`, the Discord account, all servers). The week table draws do-not-disturb as a purple band with overnight carry-over; Clear and Copy to… include it. The separate weekly do-not-disturb card is gone.
- By date: "Your day" card with the picked date's free hours, do-not-disturb and accepted events (`AvailabilityPage.MyDay`). The do-not-disturb dates form hides Repeat through until "Repeat on the following days" is ticked and shows a live summary of every date and time range before saving.

### v0.0.15 My games fixes

- Steam pictures for games released from about 2025: their header only exists under a hashed path, so a failed fixed address (`header.jpg`) falls back to `/steam-art/{appId}`, which reads `header_image` from the store's `appdetails` once and caches it (`SteamArt`). Also used on Game news; pictures that failed before the script ran are caught too.
- Steam import also reads `GetRecentlyPlayedGames` and adds games the owned list omits (Steam leaves some played free games, such as Dota 2, out of `GetOwnedGames`). Same-name entries picked from the IGDB search become the Steam game on import, keeping their mark.
- IGDB search recognises a game's Steam store link by its address (IGDB moved website kinds from `category` to `type`), so Steam games picked from search keep their Steam App ID.

### v0.0.14 sign-in, My games and do not disturb

- **Sign-in** (`/login`, `DiscordProfileClaims`, migration `20261202000100_AddUserProfiles`): a sign-in page with "Keep me signed in" (persistent cookie, 30-day sliding expiry; unticked it ends with the browser). Discord OAuth uses `prompt=none`, so people who already allowed HappyMix skip the Authorize screen. Name, display name, avatar and the Discord-connected Steam account are saved in `UserProfiles`; the cookie copy is refreshed on each request when the saved profile is newer. `POST /account/sync` ("Sync with Discord") re-reads them without signing out.
- **My games**: Steam pictures (`cdn.akamai.steamstatic.com/.../header.jpg`, placeholder on error), sorting by mark / hours played / A–Z / recently played (`?sort=`), and one **Save changes** button (`POST /games/{guild}/marks`, `SetGamePreferencesCommand`) with Undo and an unsaved-changes warning.
- **Do not disturb** (`QuietRequests`, migration `20261202000200_AddDoNotDisturb`): weekly hours and dated ranges per Discord account (all servers), edited on the Availability page (`/availability/{guild}/dnd`). Personal messages wait in `HeldMessages` (event DMs in their own outbox via `DeferAsync`) and the `release-held-messages` job sends them each minute once do-not-disturb ends, after a relevance check: open request/suggestion/invitation, still a member, player still in the game, date not canceled or past. Repeats collapse by key; messages older than 7 days are dropped. Boards show a striped purple band; `/availability who` lists the hours with 🌙.

### v0.0.13 my games, Steam and Live

- **My games** (`/games/{guild}`, `PlayerGameRequests`, migration `20261130000100_AddPlayerGames`): a per-person list with Want / Can / Won't marks. Steam links by Steam OpenID sign-in (verified), the Discord `connections` scope (verified when Discord says so) or a pasted profile (unverified). Library import needs `Steam__ApiKey` and public Game details; re-importing keeps marks, unlinking removes only imported games.
- Group pages show members' marks on the group's games and "Popular with members"; vote suggestions rank games members want first (`PollSuggestion.Wanted`).
- **Live** (`LiveRequests`, migration `20261201000100_AddLiveActivity`): a group tab with who plays now and recent sessions. Members opt in per group to share and, separately, to get DMs; organizers can also post in the group's notification channel. Notices only for the group's games or games another member marked Want/Can, one per player, game and group every 2 hours; sessions are kept 14 days.
- Live sources: Discord "Playing" statuses when `Live__DiscordPresence=true` (also turn on Presence Intent on the Developer Portal Bot page first, or Discord refuses the connection), and Steam statuses every 2 minutes for verified Steam accounts when `Steam__ApiKey` is set.
- Discord parity: game news runs through MediatR commands; new `/games`, `/watch`, `/availability who`, `/event confirm all-dates`, `/event transfer`, `/group request-join|withdraw-request|suggest-game|open|tags` and `/game-news sources`; join requests and game suggestions arrive as DMs with Accept/Decline buttons.

### CI runs and deployment diagnostics

Pull requests targeting main run validation. New PR commits cancel older in-progress checks. Main pushes do not repeat those checks; version tags run validation plus image publication and deployment. Completed checks for earlier PR commits remain in history because they validated different code.

Failed deployments collect bounded container status and up to 200 diagnostic lines selected from the last 5,000 log lines, with runtime environment values redacted. The **Production diagnostics (manual)** workflow can inspect an existing stopped or running container without rebuilding, redeploying, or modifying the database. Existing tags are immutable; retrying a tag does not incorporate newer workflow/source changes.

The v0.0.2 Linux startup failed during Discord command registration because two choice labels used a legacy source encoding. The corrective v0.0.3 release uses UTF-8 explicitly on every build platform and validates slash-command text. v0.0.2 remains an immutable tag on its original merge commit.
