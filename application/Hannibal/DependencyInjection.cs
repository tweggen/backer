using Hannibal.Configuration;
using Hannibal.Data;
using Hannibal.Services;
using Microsoft.EntityFrameworkCore;
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
            configuration.GetSection("HannibalService"));

        services.AddDbContext<HannibalContext>(options =>
            options.UseSqlite(
                configuration.GetConnectionString("HannibalDatabase")
            ));

        services.AddScoped<IHannibalService, HannibalService>();
        
        return services;
    }
}