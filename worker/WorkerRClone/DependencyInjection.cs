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


        services.AddHostedService<RCloneService>();  
        // services.AddHostedService<IBackgroundWorker, RCloneService>();
        
        return services;
    }

}