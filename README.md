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
| CS2 | NAVI, FUT Esports, Monte | 3216, 136955, 131441 |
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

`PandaScore:CounterStrikeTeamIds` and `PandaScore:Dota2TeamIds` are separate
allowlists. An empty list disables that discipline. Verify replacement IDs with
the provider before changing them; a team's name is not an identity check.

Schedules are paginated by discipline and tournament. Notifications include
starting-within-30-minutes alerts, CS2 map/Dota game results, and series results.
The schedule date uses `PandaScore:TimeZoneId` (default `Europe/Kyiv`); Discord
timestamps display in each viewer's timezone. Running matches are fetched across
date boundaries, and completed matches are selected by completion date.

Requests are paginated rather than silently capped at 100 records. Provider
failures abort the synchronization instead of publishing an incomplete empty feed.
Announcement deduplication is scoped to discipline but **remains in memory**;
restarts may resend today's schedule and results. SQLite remains the next
reliability task. This change does not implement reschedule-message edits or
per-guild subscriptions.

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

## Run with Docker

For development, use the `HappyMix.Bot Docker (Development)` Rider configuration or run:

```powershell
docker compose -f compose.development.yaml up --build
```

This sets `DOTNET_ENVIRONMENT=Development` and mounts
`src/HappyMix.Bot/appsettings.Development.json` read-only into the container.
The file must exist before starting. Development settings are excluded from published output
and the Docker build context.

Build the production image from the repository root:

```powershell
docker build --tag happymix-bot .
```

Provide the Discord token, announcement channel, and PandaScore token when the container starts:

```powershell
docker run --rm --name happymix-bot `
  --env Discord__Token="your-bot-token" `
  --env Discord__AnnouncementChannelId="your-channel-id" `
  --env PandaScore__Token="your-pandascore-token" `
  happymix-bot
```
