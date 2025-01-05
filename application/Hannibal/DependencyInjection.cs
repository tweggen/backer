using Hannibal.Configuration;
using Hannibal.Services;

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