using HappyMix.Application.Abstractions;
using HappyMix.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
