using HappyMix.Application.Abstractions;
using HappyMix.Infrastructure.Time;
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
    /// <returns>The same service collection so registrations can be chained.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // A single stateless clock is sufficient for the lifetime of the process.
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
