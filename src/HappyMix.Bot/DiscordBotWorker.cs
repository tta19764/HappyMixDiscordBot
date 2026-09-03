using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Bot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot;

public sealed partial class DiscordBotWorker(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IOptions<DiscordOptions> options,
    ILogger<DiscordBotWorker> logger) : IHostedService
{
    private readonly DiscordOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException(
                "A Discord bot token is required. Set the Discord__Token environment variable.");
        }

        client.Log += LogAsync;
        interactions.Log += LogAsync;
        client.Ready += OnReadyAsync;
        client.InteractionCreated += OnInteractionCreatedAsync;

        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);
        await client.LoginAsync(TokenType.Bot, _options.Token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.StopAsync();
        await client.LogoutAsync();
        client.Log -= LogAsync;
        interactions.Log -= LogAsync;
        client.Ready -= OnReadyAsync;
        client.InteractionCreated -= OnInteractionCreatedAsync;
    }

    private async Task OnReadyAsync()
    {
        await interactions.RegisterCommandsGloballyAsync();
        LogConnected(logger, client.CurrentUser.ToString());
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(client, interaction);
        IResult result = await interactions.ExecuteCommandAsync(context, services);

        if (!result.IsSuccess)
        {
            LogInteractionFailed(logger, interaction.Id, result.ErrorReason);
        }
    }

    private Task LogAsync(LogMessage message)
    {
        LogLevel level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.None
        };

        LogDiscordMessage(logger, level, message.Source, message.Message, message.Exception);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Connected to Discord as {CurrentUser}")]
    private static partial void LogConnected(ILogger logger, string currentUser);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Interaction {InteractionId} failed: {ErrorReason}")]
    private static partial void LogInteractionFailed(ILogger logger, ulong interactionId, string? errorReason);

    [LoggerMessage(EventId = 3, Message = "{Source}: {Message}")]
    private static partial void LogDiscordMessage(
        ILogger logger,
        LogLevel level,
        string source,
        string message,
        Exception? exception);
}
