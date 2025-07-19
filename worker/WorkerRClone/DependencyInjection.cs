using Hannibal.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkerRClone.Configuration;

namespace WorkerRClone;

public static class DependencyInjection
{
    public static IServiceCollection AddRCloneService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RCloneServiceOptions>(
            configuration.GetSection("RCloneService"));

        services.AddSingleton<IBackgroundWorker, RCloneService>();             // For DI
        services.AddHostedService(provider => 
            provider.GetRequiredService<IBackgroundWorker>());  
        // services.AddHostedService<IBackgroundWorker, RCloneService>();
        
        return services;
    }

}