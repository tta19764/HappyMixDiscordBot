using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using HappyMix.Application.Abstractions.Logs;

namespace HappyMix.Bot.Logging;

/// <summary>
/// Sends log entries to the database for the admin Logs tab, next to the usual console output. HappyMix's own entries are
/// kept from Information up, framework entries from Warning up. Entries are queued in memory (the oldest are dropped if
/// the queue fills) and written in batches by <see cref="DatabaseLogWriter"/>. Secrets are masked before anything is stored.
/// </summary>
public sealed partial class DatabaseLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, DatabaseLogger> _loggers = new(StringComparer.Ordinal);
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Set while the writer stores entries, so database logging about that work doesn't feed back into the queue.</summary>
    internal static readonly AsyncLocal<bool> Suppressed = new();

    internal ChannelReader<LogEntry> Reader => _queue.Reader;

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new DatabaseLogger(name, this));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() => _queue.Writer.TryComplete();

    /// <summary>Whether entries of <paramref name="category"/> at <paramref name="level"/> are stored.</summary>
    public static bool Stores(string category, LogLevel level)
    {
        if (level == LogLevel.None || category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            category.StartsWith("HappyMix.Bot.Logging", StringComparison.Ordinal) || category.StartsWith("System.Net.Http", StringComparison.Ordinal) && level < LogLevel.Warning)
            return false;
        return category.StartsWith("HappyMix", StringComparison.Ordinal) ? level >= LogLevel.Information : level >= LogLevel.Warning;
    }

    /// <summary>The Logs tab area of a logger category.</summary>
    public static string AreaOf(string category) => category switch
    {
        _ when category.StartsWith("HappyMix.Application", StringComparison.Ordinal) => "commands",
        _ when category.StartsWith("HappyMix.Bot.Jobs", StringComparison.Ordinal) => "jobs",
        _ when category.StartsWith("HappyMix.Bot.Web", StringComparison.Ordinal) || category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) => "dashboard",
        _ when category.Contains("Twitch", StringComparison.Ordinal) || category.Contains("Media", StringComparison.Ordinal) ||
               category.Contains("PandaScore", StringComparison.Ordinal) || category.Contains("GameNews", StringComparison.Ordinal) => "integrations",
        _ when category.StartsWith("HappyMix.Bot", StringComparison.Ordinal) || category.StartsWith("Discord", StringComparison.Ordinal) => "discord",
        _ => "system"
    };

    /// <summary>Masks tokens, keys and passwords in text that is about to be stored.</summary>
    public static string Redact(string text)
    {
        text = SecretParameter().Replace(text, match => match.Groups[1].Value + "=***");
        text = BearerToken().Replace(text, "Bearer ***");
        return DiscordToken().Replace(text, "***");
    }

    internal void Enqueue(LogEntry entry) => _queue.Writer.TryWrite(entry);

    internal IExternalScopeProvider Scopes => _scopes;

    [GeneratedRegex(@"(?i)\b(api_key|apikey|key|token|access_token|refresh_token|client_secret|secret|password)=([^&\s""']+)", RegexOptions.None, 100)]
    private static partial Regex SecretParameter();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.None, 100)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\b[MN][A-Za-z\d]{23,}\.[\w-]{6}\.[\w-]{27,}\b", RegexOptions.None, 100)]
    private static partial Regex DiscordToken();
}

internal sealed class DatabaseLogger(string category, DatabaseLoggerProvider provider) : ILogger
{
    private const int MaxMessage = 2000, MaxException = 4000, MaxValue = 300;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider.Scopes.Push(state);

    public bool IsEnabled(LogLevel logLevel) => !DatabaseLoggerProvider.Suppressed.Value && DatabaseLoggerProvider.Stores(category, logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        void Collect(object? values)
        {
            if (values is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var (key, value) in pairs)
                    if (key != "{OriginalFormat}" && value is not null)
                        properties[key] = Trim(DatabaseLoggerProvider.Redact(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""), MaxValue);
        }
        provider.Scopes.ForEachScope((scope, _) => Collect(scope), (object?)null);
        Collect(state);

        ulong? Id(string key) => properties.Remove(key, out var text) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : null;
        ulong? guild = Id("GuildId"), user = Id("UserId");
        if (eventId.Name is { Length: > 0 } name)
            properties["Event"] = name;
        string message = Trim(DatabaseLoggerProvider.Redact(formatter(state, exception)), MaxMessage);
        string? error = exception is null ? null : Trim(DatabaseLoggerProvider.Redact(exception.ToString()), MaxException);
        provider.Enqueue(new LogEntry(0, DateTimeOffset.UtcNow, (LogSeverity)logLevel, DatabaseLoggerProvider.AreaOf(category), category, message, error,
            guild, user, properties.Count == 0 ? null : JsonSerializer.Serialize(properties)));
    }

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}
