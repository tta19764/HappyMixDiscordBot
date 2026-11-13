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
| `/settings twitch` | Set up to five comma-separated Twitch handles or HTTPS channel links for one game. Use `default` to reset or `none` to hide links. |
| `/settings show` | View the settings privately. |

`/settings set` continues to save the channel and time zone together. If a server has no saved announcement channel, the deployment channel remains the fallback for its own server. Reminder mentions are limited to the configured role for that game.

The alias maps in `appsettings.json` seed each catalog on its first settings save. Each server then keeps its own copy in SQLite. Adding a team makes it available for selection. Servers using the catalog as their followed list also begin following the added team. A server with an explicit followed list can select the new team with `/settings teams`.

Schedules and reminders show **Watch** when PandaScore lists a preferred Twitch channel for the match. Otherwise they show the preferred links as **Twitch channels**. Defaults are `cs2_maincast` and `cs2_maincast2` for CS2, and `ghostik` and `dota2_maincast` for Dota 2.

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

Use `/availability add` with a date, time and UTC offset for both endpoints, for example
`2026-11-13 22:00 +02:00` through `2026-11-14 01:00 +02:00`. Discord displays those times
in each viewer's local timezone. Use `/availability list` to get the ID for `edit` or `remove`.

### Live match-start alerts

The bot posts a start alert the first time PandaScore reports a followed series as running.
Use the `match-starts` option of `/settings notifications` to control these alerts.
Set `Twitch__ClientId` and `Twitch__ClientSecret` in the deployment environment
(`/opt/happymix/bot.env` on the VM) using credentials from a Twitch developer application.
Start alerts link only preferred channels that Twitch reports live in the matching game category
when the bot sends the message. A live channel can be showing a different match.
The alert still posts if Twitch credentials are missing or the lookup fails.
