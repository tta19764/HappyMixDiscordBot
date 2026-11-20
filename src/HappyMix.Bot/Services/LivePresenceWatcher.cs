using Discord;
using Discord.WebSocket;
using HappyMix.Application.Live;
using MediatR;

namespace HappyMix.Bot.Services;

/// <summary>
/// Turns Discord "Playing" statuses into Live sessions, for people who share with a group only. Discord only sends
/// statuses when the privileged Presence intent is on (<c>Live:DiscordPresence</c>, plus the switch in the Discord
/// Developer Portal); without it this watcher stays idle and Live relies on Steam.
/// </summary>
public sealed partial class LivePresenceWatcher(DiscordSocketClient discord, ISender sender, IConfiguration configuration,
    ILogger<LivePresenceWatcher> logger) : IHostedService, IDisposable
{
    public void Dispose() => _refresh.Dispose();

    private static readonly TimeSpan SharersAge = TimeSpan.FromMinutes(2);
    private HashSet<ulong> _sharers = [];
    private DateTimeOffset _loaded = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refresh = new(1);

    public static bool Enabled(IConfiguration configuration) => configuration.GetValue<bool>("Live:DiscordPresence");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled(configuration))
            discord.PresenceUpdated += OnPresenceUpdatedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        discord.PresenceUpdated -= OnPresenceUpdatedAsync;
        return Task.CompletedTask;
    }

    /// <summary>The game in a status: the first "Playing" activity, if any.</summary>
    public static string? Playing(IPresence? presence) =>
        presence?.Activities?.FirstOrDefault(activity => activity.Type == ActivityType.Playing)?.Name;

    private async Task OnPresenceUpdatedAsync(SocketUser user, SocketPresence before, SocketPresence after)
    {
        if (user.IsBot)
            return;
        string? game = Playing(after);
        if (game == Playing(before))
            return;
        try
        {
            if (!(await SharersAsync()).Contains(user.Id))
                return;
            // The same change arrives once per shared server; the command ignores repeats of an open session.
            await sender.Send(new ReportPlayingCommand(user.Id, game, "discord"));
        }
        catch (Exception error)
        {
            LogFailed(logger, user.Id, error);
        }
    }

    private async Task<HashSet<ulong>> SharersAsync()
    {
        if (DateTimeOffset.UtcNow - _loaded < SharersAge)
            return _sharers;
        await _refresh.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _loaded >= SharersAge)
            {
                _sharers = [.. (await sender.Send(new GetLiveSharersQuery())).Value];
                _loaded = DateTimeOffset.UtcNow;
            }
            return _sharers;
        }
        finally
        {
            _refresh.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live status for user {UserId} couldn't be recorded")]
    private static partial void LogFailed(ILogger logger, ulong userId, Exception error);
}
