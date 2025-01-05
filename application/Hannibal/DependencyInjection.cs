using Hannibal.Configuration;
using Hannibal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hannibal;

public static class DependencyInjection
{
    public static IServiceCollection AddHannibalService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HannibalServiceOptions>(
            configuration.GetSection("BackupService"));

        services.AddScoped<IHannibalService, HannibalService>();
        
        return services;
    }
}