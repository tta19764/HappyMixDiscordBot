using Microsoft.Extensions.DependencyInjection.Extensions;
using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Guilds;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Infrastructure.Persistence;
using HappyMix.Infrastructure.PandaScore;
using HappyMix.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HappyMix.Infrastructure;

/// <summary>
/// Provides dependency-injection registrations for infrastructure implementations.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers implementations of the external-service abstractions defined by the Application layer.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <param name="configuration">The configuration containing PandaScore connection settings.</param>
    /// <returns>The same service collection so registrations can be chained.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // A single stateless clock is sufficient for the lifetime of the process.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(provider => new GameNewsStore(provider.GetRequiredService<IDbContextFactory<HappyMixDbContext>>()));
        services.AddSingleton<HappyMix.Application.Abstractions.News.IGameNewsStore>(provider => provider.GetRequiredService<GameNewsStore>());
        services.AddSingleton(provider => new NotificationRouteStore(provider.GetRequiredService<IDbContextFactory<HappyMixDbContext>>()));
        // Account links, groups and server settings are read on almost every request: they go through a per-server
        // cache that every write of these stores clears (see Caching/GuildCache.cs).
        services.AddHybridCache();
        services.AddSingleton<HappyMix.Infrastructure.Caching.GuildCache>();
        services.AddSingleton<SqliteGuildSettingsStore>();
        services.AddSingleton<SqliteGuildGroupStore>();
        services.AddSingleton<SqliteAccountLinkStore>();
        services.AddSingleton<IGuildSettingsStore>(provider => new HappyMix.Infrastructure.Caching.CachedGuildSettingsStore(
            provider.GetRequiredService<SqliteGuildSettingsStore>(), provider.GetRequiredService<HappyMix.Infrastructure.Caching.GuildCache>()));
        services.AddSingleton<IGuildGroupStore>(provider => new HappyMix.Infrastructure.Caching.CachedGuildGroupStore(
            provider.GetRequiredService<SqliteGuildGroupStore>(), provider.GetRequiredService<HappyMix.Infrastructure.Caching.GuildCache>()));
        services.AddSingleton<IAvailabilityStore, SqliteAvailabilityStore>();
        services.AddSingleton<IAccountLinkStore>(provider => new HappyMix.Infrastructure.Caching.CachedAccountLinkStore(
            provider.GetRequiredService<SqliteAccountLinkStore>(), provider.GetRequiredService<HappyMix.Infrastructure.Caching.GuildCache>()));
        services.AddSingleton<HappyMix.Application.Abstractions.Polls.IPollStore, SqlitePollStore>();
        services.AddSingleton<HappyMix.Application.Abstractions.Watch.IWatchStore, SqliteWatchStore>();
        services.AddSingleton<HappyMix.Application.Abstractions.Logs.ILogStore, SqliteLogStore>();
        services.AddSingleton<HappyMix.Application.Abstractions.Players.IPlayerGameStore, SqlitePlayerGameStore>();
        services.AddSingleton<HappyMix.Application.Abstractions.Live.ILiveStore, SqliteLiveStore>();
        services.AddSingleton<HappyMix.Application.Abstractions.Profiles.IUserProfileStore, SqliteUserProfileStore>();
        services.AddSingleton<HappyMix.Application.Abstractions.Quiet.IQuietStore, SqliteQuietStore>();
        services.AddSingleton<HappyMix.Application.Abstractions.Stats.IGameStatsStore, SqliteGameStatsStore>();
        services.AddHttpClient(HappyMix.Infrastructure.Stats.GameStatsDirectory.OpenDotaClient, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(HappyMix.Infrastructure.Stats.GameStatsDirectory.OpenDotaSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HappyMixBot/1.0");
        });
        services.AddHttpClient(HappyMix.Infrastructure.Stats.GameStatsDirectory.FaceitClient, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<HappyMix.Infrastructure.Stats.GameStatsDirectory>();
        services.AddSingleton<HappyMix.Application.Abstractions.Stats.IGameStatsDirectory>(provider =>
            provider.GetRequiredService<HappyMix.Infrastructure.Stats.GameStatsDirectory>());
        services.AddSingleton<HappyMix.Application.Abstractions.Stats.IDotaMetaSource>(provider =>
            provider.GetRequiredService<HappyMix.Infrastructure.Stats.GameStatsDirectory>());
        services.AddSingleton<HappyMix.Application.Abstractions.Stats.IHeroMetaSource>(provider =>
            provider.GetRequiredService<HappyMix.Infrastructure.Stats.GameStatsDirectory>());
        // STRATZ asks API clients to identify as STRATZ_API.
        services.AddHttpClient(HappyMix.Infrastructure.Stats.GameStatsDirectory.StratzClient, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("STRATZ_API");
        });
        services.AddSingleton<HappyMix.Application.Abstractions.Stats.IGameFeatureStore, SqliteGameFeatureStore>();
        services.AddHttpClient(HappyMix.Infrastructure.Steam.SteamDirectory.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HappyMixBot/1.0");
        });
        services.AddSingleton<HappyMix.Application.Abstractions.Players.ISteamDirectory, HappyMix.Infrastructure.Steam.SteamDirectory>();
        services.AddHttpClient(HappyMix.Infrastructure.Media.MediaCatalog.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HappyMixBot/1.0");
        });
        services.AddSingleton<HappyMix.Application.Abstractions.Media.IMediaCatalog, HappyMix.Infrastructure.Media.MediaCatalog>();
        services.AddSingleton<HappyMix.Application.Abstractions.Events.IEventStore, SqliteEventStore>();
        services.AddSingleton<IMatchAnnouncementStateFactory, SqliteMatchAnnouncementStateFactory>();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName));
        services.AddDbContextFactory<HappyMixDbContext>((provider, options) =>
        {
            var storage = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            string path = Path.GetFullPath(storage.DatabasePath);
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 30
            };
            options.UseSqlite(connection.ToString());
        });
        services.AddSingleton<IMatchAnnouncementState>(provider => new SqliteMatchAnnouncementState(
            provider.GetRequiredService<IDbContextFactory<HappyMixDbContext>>(),
            provider.GetRequiredService<IOptions<StorageOptions>>(),
            configuration.GetValue<string>("Discord:AnnouncementChannelId") ?? "default"));

        services
            .AddOptions<PandaScoreOptions>()
            .Bind(configuration.GetSection(PandaScoreOptions.SectionName));
        services.AddHttpClient<PandaScoreClient>((provider, client) =>
        {
            var options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<PandaScoreOptions>>()
                .Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });
        services.AddTransient<IPandaScoreClient>(provider => provider.GetRequiredService<PandaScoreClient>());
        services.AddTransient<IGuildMatchFeed>(provider => provider.GetRequiredService<PandaScoreClient>());
        services.AddHttpClient(PandaScoreTeamDirectory.HttpClientName, (provider, client) =>
            client.BaseAddress = new Uri(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PandaScoreOptions>>().Value.BaseUrl));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEsportsTeamDirectory, PandaScoreTeamDirectory>();

        return services;
    }

    /// <summary>Creates the data directory and applies versioned migrations before workers start.</summary>
    /// <param name="services">The application's service provider.</param>
    /// <param name="cancellationToken">Cancels migration work.</param>
    /// <returns>A task completing when storage is ready; failures stop startup.</returns>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(storage.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storage.DatabasePath))!);
        var factory = services.GetRequiredService<IDbContextFactory<HappyMixDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }
}
