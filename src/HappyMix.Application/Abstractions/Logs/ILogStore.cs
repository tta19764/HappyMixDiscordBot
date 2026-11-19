namespace HappyMix.Application.Abstractions.Logs;

/// <summary>Log levels as stored: the numeric values of Microsoft.Extensions.Logging.LogLevel.</summary>
public enum LogSeverity { Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5 }

/// <summary>
/// One stored log entry. <see cref="GuildId"/> ties it to a server, so that server's administrators can see it;
/// entries without a server are bot-wide and only shown to the bot's owners. <see cref="Area"/> groups entries for
/// filtering (commands, Discord, jobs, dashboard, catalogs, system).
/// </summary>
public sealed record LogEntry(
    long Id,
    DateTimeOffset At,
    LogSeverity Level,
    string Area,
    string Category,
    string Message,
    string? Exception,
    ulong? GuildId,
    ulong? UserId,
    string? Properties);

/// <summary>Which entries to read, newest first. <see cref="Before"/> continues a page after the entry with that id.</summary>
public sealed record LogFilter(
    ulong? GuildId,
    bool IncludeBotWide,
    LogSeverity MinimumLevel = LogSeverity.Information,
    string? Area = null,
    string? Search = null,
    DateTimeOffset? Since = null,
    long? Before = null,
    long? After = null,
    int Limit = 100);

public interface ILogStore
{
    Task WriteAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken cancellationToken);
    Task<IReadOnlyList<LogEntry>> ReadAsync(LogFilter filter, CancellationToken cancellationToken);

    /// <summary>Deletes entries older than <paramref name="olderThan"/> and the oldest beyond <paramref name="maxEntries"/>.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, int maxEntries, CancellationToken cancellationToken);
}
