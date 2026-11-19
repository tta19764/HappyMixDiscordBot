using System.Reflection;
using HappyMix.Application.Guilds;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application;

/// <summary>
/// Provides dependency-injection registrations for the Application layer.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers application use cases and the MediatR dispatcher.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <returns>The same service collection so registrations can be chained.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Scanning this assembly keeps request handlers private to the Application
        // layer while allowing adapters to dispatch requests through MediatR.
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
            // Every command is logged with its server, user and outcome for the admin Logs tab.
            configuration.AddOpenBehavior(typeof(Logging.CommandLoggingBehavior<,>));
        });
        services.AddSingleton<GroupRoles>();

        return services;
    }
}
