namespace HappyMix.Infrastructure.Persistence;

/// <summary>Configures durable storage and the initial migration from process-local history.</summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section containing persistence settings.</summary>
    public const string SectionName = "Storage";

    /// <summary>SQLite file path, relative to the process working directory unless absolute.</summary>
    public string DatabasePath { get; init; } = "data/happymix.db";

    /// <summary>
    /// When false, first successful synchronization seeds already completed results without sending them.
    /// When true, first synchronization replays available results, matching the former in-memory behavior.
    /// Subsequent starts always use persisted history, irrespective of this setting.
    /// </summary>
    public bool ReplayExistingResultsOnFirstRun { get; init; }
}
