using Higgins.Configuration;
using Higgins.Data;
using Higgins.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Higgins;

public static class DependencyInjection
{
    public static IServiceCollection AddHigginsService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<HigginsContext>(options =>
            options.UseSqlite(
                configuration.GetConnectionString("HigginsDatabase")
            ));

        services.Configure<HigginsServiceOptions>(
            configuration.GetSection("HigginsService"));

        services.AddScoped<IHigginsService, HigginsService>();
        
        return services;
    }
}