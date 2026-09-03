# HappyMix Discord Bot

A Discord bot for the HappyMix server, built with Discord.Net, .NET 10, and Clean Architecture.

Quartz.NET runs scheduled background jobs. The initial health-check job executes on startup and every 15 minutes.

## Project structure

- `src/HappyMix.Domain` contains enterprise rules and domain models. It has no project dependencies.
- `src/HappyMix.Application` contains use cases and abstractions. It depends only on Domain.
- `src/HappyMix.Infrastructure` implements Application abstractions for external concerns.
- `src/HappyMix.Bot` is the Discord.Net adapter and composition root.
- `tests/HappyMix.Application.Tests` contains application unit tests.

## Run locally

Create a bot in the Discord Developer Portal, then provide its token through configuration:

```powershell
$env:Discord__Token = "your-bot-token"
dotnet run --project src/HappyMix.Bot
```

The bot registers a global `/ping` slash command after connecting. Global command changes can take time to propagate in Discord.

Never commit the bot token. For local development, environment variables or .NET user secrets are recommended.
