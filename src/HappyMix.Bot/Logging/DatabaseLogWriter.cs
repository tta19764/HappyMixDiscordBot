using HappyMix.Application.Abstractions.Logs;

namespace HappyMix.Bot.Logging;

/// <summary>
/// Writes queued log entries to the database in batches, and removes entries older than <c>Logs:RetentionDays</c>
/// (default 14) or beyond <c>Logs:MaxEntries</c> (default 50,000) once an hour. A failed write drops that batch rather
/// than retrying forever, so logging can never block the bot.
/// </summary>
public sealed class DatabaseLogWriter(DatabaseLoggerProvider provider, ILogStore store, IConfiguration configuration) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DatabaseLoggerProvider.Suppressed.Value = true;
        var batch = new List<LogEntry>(BatchSize);
        var nextPrune = DateTimeOffset.UtcNow.AddMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromSeconds(2));
                if (await provider.Reader.WaitToReadAsync(wait.Token))
                    while (batch.Count < BatchSize && provider.Reader.TryRead(out var entry))
                        batch.Add(entry);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Two seconds passed: write what there is.
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(batch, stoppingToken);
            if (DateTimeOffset.UtcNow >= nextPrune)
            {
                nextPrune = DateTimeOffset.UtcNow.AddHours(1);
                int days = Math.Clamp(configuration.GetValue("Logs:RetentionDays", 14), 1, 365);
                int max = Math.Clamp(configuration.GetValue("Logs:MaxEntries", 50_000), 1_000, 1_000_000);
                try { await store.PruneAsync(DateTimeOffset.UtcNow.AddDays(-days), max, stoppingToken); }
                catch (Exception) when (!stoppingToken.IsCancellationRequested) { /* try again next hour */ }
            }
        }
        // Keep what was logged during shutdown.
        while (provider.Reader.TryRead(out var entry))
            batch.Add(entry);
        await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<LogEntry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;
        try
        {
            await store.WriteAsync(batch, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The database is unavailable; the console log still has these entries.
        }
        batch.Clear();
    }
}
