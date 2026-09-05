# HappyMix Discord Bot

A Discord bot for the HappyMix server, built with Discord.Net, .NET 10, and Clean Architecture.

Quartz.NET polls PandaScore every minute for the day's Counter-Strike 2 matches and announcement changes.
MediatR dispatches requests from the Discord and Quartz adapters to Application handlers.

PandaScore S- and A-tier tournaments are included in schedules, starting-soon alerts,
and results by default. This includes BLAST Open, which PandaScore labels A-tier.
Lower or unknown tiers are excluded. Configure `PandaScore:IncludedTournamentTiers`
as an array in appsettings to change the accepted tiers (default: `["s", "a"]`).
Daily schedules use separate pages for each tournament, ordered by match start time.
Long tournament schedules continue on additional pages within Discord's embed limit.
Tier definitions: https://developers.pandascore.co/docs/tournaments-in-depth

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
