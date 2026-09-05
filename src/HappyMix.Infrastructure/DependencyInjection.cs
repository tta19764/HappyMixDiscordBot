using HappyMix.Application.Abstractions;
using HappyMix.Application.Abstractions.Esports;
using HappyMix.Application.Abstractions.Notifications;
using HappyMix.Infrastructure.Notifications;
using HappyMix.Infrastructure.PandaScore;
using HappyMix.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<IMatchAnnouncementState, InMemoryMatchAnnouncementState>();

        services
            .AddOptions<PandaScoreOptions>()
            .Bind(configuration.GetSection(PandaScoreOptions.SectionName));
        services.AddHttpClient<IPandaScoreClient, PandaScoreClient>((provider, client) =>
        {
            var options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<PandaScoreOptions>>()
                .Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        return services;
    }
}
