using Discord.Rest;
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
        interactions.InteractionExecuted += OnInteractionExecutedAsync;
        client.PollVoteAdded += OnPollVoteAddedAsync;
        client.PollVoteRemoved += OnPollVoteRemovedAsync;
        client.JoinedGuild += OnJoinedGuildAsync;
        client.LeftGuild += OnLeftGuildAsync;
        client.Disconnected += OnDisconnectedAsync;

        // Modules are discovered once during startup and activated through the same
        // container that owns Application and Infrastructure dependencies.
        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);
        await client.LoginAsync(TokenType.Bot, _options.Token);
        await client.StartAsync();
    }

    private Task OnPollVoteAddedAsync(Cacheable<IUser, ulong> user, Cacheable<ISocketMessageChannel, IRestMessageChannel, IMessageChannel, ulong> channel,
        Cacheable<IUserMessage, ulong> message, Cacheable<SocketGuild, RestGuild, IGuild, ulong>? guild, ulong answerId) =>
        CopyPollVoteAsync(user.Id, message.Id, guild?.Id, answerId, added: true);

    private Task OnPollVoteRemovedAsync(Cacheable<IUser, ulong> user, Cacheable<ISocketMessageChannel, IRestMessageChannel, IMessageChannel, ulong> channel,
        Cacheable<IUserMessage, ulong> message, Cacheable<SocketGuild, RestGuild, IGuild, ulong>? guild, ulong answerId) =>
        CopyPollVoteAsync(user.Id, message.Id, guild?.Id, answerId, added: false);

    /// <summary>Copies a vote cast with Discord's poll controls into the bot's vote, off the gateway thread.</summary>
    private Task CopyPollVoteAsync(ulong userId, ulong messageId, ulong? guildId, ulong answerId, bool added)
    {
        if (guildId is not { } guild)
            return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try
            {
                await services.GetRequiredService<PollMessages>().OnNativeVoteAsync(guild, userId, messageId, answerId, added);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogPollVoteFailed(logger, messageId, guild, error);
            }
        });
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Copying a Discord poll vote for message {MessageId} in server {GuildId} failed; the next sync catches up")]
    private static partial void LogPollVoteFailed(ILogger logger, ulong messageId, ulong guildId, Exception error);

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop receiving gateway events before releasing local subscriptions.
        await client.StopAsync();
        await client.LogoutAsync();

        // Removing handlers prevents retained references if the host is restarted in-process.
        client.Log -= LogAsync;
        client.PollVoteAdded -= OnPollVoteAddedAsync;
        client.PollVoteRemoved -= OnPollVoteRemovedAsync;
        client.JoinedGuild -= OnJoinedGuildAsync;
        client.LeftGuild -= OnLeftGuildAsync;
        client.Disconnected -= OnDisconnectedAsync;
        interactions.Log -= LogAsync;
        client.Ready -= OnReadyAsync;
        client.InteractionCreated -= OnInteractionCreatedAsync;
        interactions.InteractionExecuted -= OnInteractionExecutedAsync;
    }

    /// <summary>"/settings broadcasts", or the button/menu id prefix such as "poll:pick", without per-message ids.</summary>
    internal static string InteractionName(ICommandInfo? command, IDiscordInteraction interaction) => interaction switch
    {
        ISlashCommandInteraction slash => "/" + string.Join(" ", new[] { slash.Data.Name }.Concat(SubcommandPath(slash.Data.Options))),
        IComponentInteraction component => string.Join(':', component.Data.CustomId.Split(':').Take(2)),
        _ => command?.Name ?? interaction.Type.ToString()
    };

    private static IEnumerable<string> SubcommandPath(IReadOnlyCollection<IApplicationCommandInteractionDataOption> options)
    {
        var first = options.FirstOrDefault();
        return first?.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup
            ? new[] { first.Name }.Concat(SubcommandPath(first.Options))
            : [];
    }

    private Task OnJoinedGuildAsync(SocketGuild guild)
    {
        LogJoinedGuild(logger, guild.Id, guild.Name, guild.MemberCount);
        return Task.CompletedTask;
    }

    private Task OnLeftGuildAsync(SocketGuild guild)
    {
        LogLeftGuild(logger, guild.Id, guild.Name);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception error)
    {
        LogDisconnected(logger, error.GetType().Name, error.Message);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 20, EventName = "InteractionUsed", Level = LogLevel.Information,
        Message = "{Interaction} used by user {UserId} in server {GuildId}")]
    private static partial void LogInteractionUsed(ILogger logger, string interaction, ulong userId, ulong? guildId);

    [LoggerMessage(EventId = 21, EventName = "InteractionError", Level = LogLevel.Warning,
        Message = "{Interaction} by user {UserId} in server {GuildId} failed: {ErrorType} {Reason}")]
    private static partial void LogInteractionError(ILogger logger, string interaction, ulong userId, ulong? guildId, string errorType, string? reason);

    [LoggerMessage(EventId = 22, EventName = "JoinedServer", Level = LogLevel.Information, Message = "Joined server {GuildId} ({GuildName}, {MemberCount} members)")]
    private static partial void LogJoinedGuild(ILogger logger, ulong guildId, string guildName, int memberCount);

    [LoggerMessage(EventId = 23, EventName = "LeftServer", Level = LogLevel.Warning, Message = "Removed from server {GuildId} ({GuildName})")]
    private static partial void LogLeftGuild(ILogger logger, ulong guildId, string guildName);

    [LoggerMessage(EventId = 24, EventName = "Disconnected", Level = LogLevel.Warning, Message = "Disconnected from Discord ({ErrorType}): {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string errorType, string reason);

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
        var result = await interactions.ExecuteCommandAsync(context, services);

        if (!result.IsSuccess)
        {
            LogInteractionFailed(logger, interaction.Id, result.ErrorReason);
        }
    }

    // Handlers run asynchronously, so their exceptions only arrive here. Log them and tell the person instead of
    // leaving Discord's "This interaction failed" (or nothing at all, after a defer).
    private async Task OnInteractionExecutedAsync(ICommandInfo? command, IInteractionContext context, Discord.Interactions.IResult result)
    {
        string name = InteractionName(command, context.Interaction);
        if (result.IsSuccess)
        {
            LogInteractionUsed(logger, name, context.User.Id, context.Guild?.Id);
            return;
        }
        LogInteractionError(logger, name, context.User.Id, context.Guild?.Id, result.Error?.ToString() ?? "", result.ErrorReason);
        const string message = "Something went wrong. Please try again, or answer from the dashboard.";
        try
        {
            if (context.Interaction.HasResponded)
                await context.Interaction.FollowupAsync(message, ephemeral: true);
            else
                await context.Interaction.RespondAsync(message, ephemeral: true);
        }
        catch (Discord.Net.HttpException)
        {
            // The interaction token may have expired; the failure is already logged.
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
