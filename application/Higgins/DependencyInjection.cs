using Higgins.Configuration;
using Higgins.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Higgins;

public static class DependencyInjection
{
    public static IServiceCollection AddHannibalService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HigginsServiceOptions>(
            configuration.GetSection("HigginsService"));

        services.AddScoped<IHigginsService, HigginsService>();
        
        return services;
    }
}