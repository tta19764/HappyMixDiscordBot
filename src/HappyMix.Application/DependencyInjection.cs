using HappyMix.Application.Status;
using Microsoft.Extensions.DependencyInjection;

namespace HappyMix.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddTransient<GetBotStatusHandler>();
        return services;
    }
}
