using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using HappyMix.Bot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HappyMix.Bot.Services;

/// <summary>
/// Owns the Discord gateway connection and routes incoming interactions through Discord.Net.
/// </summary>
/// <param name="client">The shared Discord gateway client.</param>
/// <param name="interactions">The service that discovers and executes interaction modules.</param>
/// <param name="services">The root service provider used to activate interaction modules.</param>
/// <param name="options">The configured Discord credentials and connection settings.</param>
/// <param name="logger">The application logger for lifecycle and Discord.Net events.</param>
public sealed partial class DiscordBotWorker(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IOptions<DiscordOptions> options,
    ILogger<DiscordBotWorker> logger) : IHostedService
{
    private readonly DiscordOptions _options = options.Value;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException(
                "A Discord bot token is required. Set the Discord__Token environment variable.");
        }

        // Subscribe before connecting so startup and early gateway messages are not lost.
        client.Log += LogAsync;
        interactions.Log += LogAsync;
        client.Ready += OnReadyAsync;
        client.InteractionCreated += OnInteractionCreatedAsync;

        // Modules are discovered once during startup and activated through the same
        // container that owns Application and Infrastructure dependencies.
        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);
        await client.LoginAsync(TokenType.Bot, _options.Token);
        await client.StartAsync();
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop receiving gateway events before releasing local subscriptions.
        await client.StopAsync();
        await client.LogoutAsync();

        // Removing handlers prevents retained references if the host is restarted in-process.
        client.Log -= LogAsync;
        interactions.Log -= LogAsync;
        client.Ready -= OnReadyAsync;
        client.InteractionCreated -= OnInteractionCreatedAsync;
    }

    private async Task OnReadyAsync()
    {
        // Global registration is appropriate for production. Discord can take time
        // to propagate global command changes to every guild.
        await interactions.RegisterCommandsGloballyAsync();
        LogConnected(logger, client.CurrentUser);
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        // The context connects the raw gateway interaction with Discord.Net's
        // command executor and the application's dependency-injection container.
        var context = new SocketInteractionContext(client, interaction);
        IResult result = await interactions.ExecuteCommandAsync(context, services);

        if (!result.IsSuccess)
        {
            LogInteractionFailed(logger, interaction.Id, result.ErrorReason);
        }
    }

    private Task LogAsync(LogMessage message)
    {
        // Normalize Discord.Net severity levels into Microsoft.Extensions.Logging
        // so all output follows the host's configured providers and filters.
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
    private static partial void LogConnected(ILogger logger, SocketSelfUser currentUser);

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
