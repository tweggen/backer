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
        services.Configure<HigginsServiceOptions>(
            configuration.GetSection("HigginsService"));

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Backer",
            "higgins.db"
        );
        
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
        var connectionString = $"Data Source={dbPath}";

        services.AddDbContext<HigginsContext>(options =>
            options.UseSqlite(
                //configuration.GetConnectionString("HigginsDatabase")
                connectionString
            ));

        services.AddScoped<IHigginsService, HigginsService>();
        
        return services;
    }
}