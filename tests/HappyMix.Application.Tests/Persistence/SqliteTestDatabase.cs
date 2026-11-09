using HappyMix.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application.Tests.Persistence;

/// <summary>Owns a real temporary SQLite file and independently restartable service providers.</summary>
internal sealed class SqliteTestDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HappyMix-tests", Guid.NewGuid().ToString("N"));
    private readonly List<ServiceProvider> _providers = [];

    public ServiceProvider Services { get; private set; } = null!;
    public string DatabasePath => Path.Combine(_directory, "nested", "history.db");

    public static async Task<SqliteTestDatabase> CreateAsync(bool replay = false)
    {
        var database = new SqliteTestDatabase();
        try
        {
            database.Services = await database.OpenAsync(replay);
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    public async Task<ServiceProvider> OpenAsync(bool replay = false, string channel = "123")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabasePath"] = DatabasePath,
            ["Storage:ReplayExistingResultsOnFirstRun"] = replay.ToString(),
            ["Discord:AnnouncementChannelId"] = channel
        }).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        await provider.InitializeDatabaseAsync();
        return provider;
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        // Only this fixture's verified, unique temporary directory is ever removed.
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
